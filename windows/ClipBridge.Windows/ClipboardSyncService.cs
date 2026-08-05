using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ClipBridge.Windows;

public sealed class ClipboardSyncService
{
    private const int Port = 45837;
    private readonly string _code; private readonly Action<string> _status;
    private readonly string _deviceId = Guid.NewGuid().ToString();
    private readonly HashSet<string> _seen = [];
    private readonly object _seenGate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = [];
    private readonly HashSet<TcpClient> _verifiedClients = [];
    private readonly Channel<OutboundMessage> _outbound = Channel.CreateUnbounded<OutboundMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ClipboardActor _clipboardActor;
    private TcpClient? _activeClient;
    private volatile string? _lastText;
    public ClipboardSyncService(string code, Action<string> status)
    {
        _code = code;
        _status = status;
        _clipboardActor = new ClipboardActor(OnLocalClipboardRead, OnClipboardWriteCompleted);
        _ = SendLoopAsync();
    }

    public async Task StartAsync()
    {
        _ = AcceptLoopAsync();
        _status("已启动，正在等待 Android 连接…"); await Task.CompletedTask;
    }
    public async Task StopAsync()
    {
        _cts.Cancel();
        _outbound.Writer.TryComplete();
        TcpClient[] clients;
        lock (_clients)
        {
            clients = _clients.ToArray();
            _clients.Clear();
            _verifiedClients.Clear();
            _activeClient = null;
        }
        foreach (var client in clients) client.Dispose();
        _clipboardActor.Dispose();
        await Task.CompletedTask;
    }
    public void NotifyClipboardChanged() => _clipboardActor.RequestRead();

    private async Task AcceptLoopAsync()
    {
        var listener = new TcpListener(IPAddress.Any, Port); listener.Start();
        try { while (!_cts.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(_cts.Token); AddClient(client); } } catch (OperationCanceledException) { } finally { listener.Stop(); }
    }
    private void AddClient(TcpClient client)
    {
        lock (_clients)
        {
            foreach (var previous in _clients.ToArray()) previous.Dispose();
            _clients.Clear();
            _verifiedClients.Clear();
            _clients.Add(client);
            _activeClient = client;
        }
        _status("网络已连接，正在验证配对码…");
        _ = ReadLoopAsync(client);
        QueueSend(client, new Hello(_deviceId, Environment.MachineName, Proof($"hello|{_deviceId}|1")));
    }
    private async Task ReadLoopAsync(TcpClient client)
    {
        try { using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 1_048_576, true); while (!_cts.IsCancellationRequested) { var line = await reader.ReadLineAsync(_cts.Token); if (line is null) break; await HandleAsync(line, client); } }
        catch { }
        finally
        {
            var wasActive = false;
            lock (_clients)
            {
                _clients.Remove(client);
                _verifiedClients.Remove(client);
                if (ReferenceEquals(_activeClient, client)) { _activeClient = null; wasActive = true; }
            }
            client.Dispose();
            if (wasActive && !_cts.IsCancellationRequested) _status("Android 已断开，正在等待重新连接…");
        }
    }
    private void OnLocalClipboardRead(string text)
    {
        if (text == _lastText) return;
        _lastText = text;
        if (
            text.StartsWith("clipbridge:png:", StringComparison.Ordinal) ||
            text.StartsWith("clipbridge:jpeg:", StringComparison.Ordinal) ||
            text.StartsWith("clipbridge:gif:", StringComparison.Ordinal) ||
            text.StartsWith("clipbridge:image:", StringComparison.Ordinal)
        )
            _status("已读取 Windows 图片，正在发送到 Android…");
        var message = new Clip(Guid.NewGuid().ToString(), _deviceId, text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        MarkSeen(message.Id);
        Broadcast(new SignedClip(message, Proof(message.Canonical)));
    }
    private Task HandleAsync(string line, TcpClient client)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var type = document.RootElement.GetProperty("Type").GetString();
            if (type == "ping") { QueueSend(client, new ControlMessage("pong")); return Task.CompletedTask; }
            if (type == "hello")
            {
                var hello = JsonSerializer.Deserialize<Hello>(line);
                if (hello is null || hello.Proof != Proof($"hello|{hello.DeviceId}|1"))
                {
                    _status("连接已建立，但配对码不一致。");
                    client.Dispose();
                    return Task.CompletedTask;
                }
                lock (_clients)
                {
                    if (!_clients.Contains(client)) return Task.CompletedTask;
                    _verifiedClients.Add(client);
                }
                _status($"已连接并通过配对验证：{hello.DeviceName}。现在复制一段新文字测试。");
                return Task.CompletedTask;
            }
            var signed = JsonSerializer.Deserialize<SignedClip>(line);
            lock (_clients) { if (!_verifiedClients.Contains(client)) return Task.CompletedTask; }
            if (signed?.Type != "clip" ||
                signed.Message.OriginDeviceId == _deviceId ||
                signed.Mac != Proof(signed.Message.Canonical) ||
                !MarkSeen(signed.Message.Id))
                return Task.CompletedTask;
            _lastText = signed.Message.Text;
            _clipboardActor.Enqueue(signed.Message.Text);
        }
        catch (Exception exception) { _status($"剪贴板处理失败：{exception.Message}"); }
        return Task.CompletedTask;
    }
    private void Broadcast(SignedClip packet)
    {
        TcpClient[] targets;
        lock (_clients)
            targets = _clients.Where(_verifiedClients.Contains).ToArray();
        foreach (var client in targets) QueueSend(client, packet);
    }

    private void QueueSend<T>(TcpClient client, T value)
    {
        var payload = JsonSerializer.Serialize(value) + "\n";
        _outbound.Writer.TryWrite(new OutboundMessage(client, payload));
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in _outbound.Reader.ReadAllAsync(_cts.Token))
            {
                lock (_clients)
                    if (!_clients.Contains(message.Client)) continue;
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(message.Payload);
                    await message.Client.GetStream().WriteAsync(bytes, _cts.Token);
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
    }
    private bool MarkSeen(string id) { lock (_seenGate) return _seen.Add(id); }
    private void OnClipboardWriteCompleted(ClipboardWriteResult result) =>
        _status(result.Success
            ? $"已写入 Windows 剪贴板（{result.Text.Length} 个字符，{result.ElapsedMilliseconds} ms，重试 {result.Attempts - 1} 次）。"
            : $"已收到 Android 内容，但 Windows 剪贴板持续被占用（{result.ElapsedMilliseconds} ms）：{result.Error}");
    private string Proof(string value) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_code), Encoding.UTF8.GetBytes(value)));
    private record Hello(string DeviceId, string DeviceName, string Proof) { public string Type => "hello"; public int Version => 1; }
    private record ControlMessage(string Type);
    private record OutboundMessage(TcpClient Client, string Payload);
    private record Clip(string Id, string OriginDeviceId, string Text, long SentAt) { public string Canonical => $"clip|{Id}|{OriginDeviceId}|{Text}|{SentAt}"; }
    private record SignedClip(Clip Message, string Mac) { public string Type => "clip"; }
}
