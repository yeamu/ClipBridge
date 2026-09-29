using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace ClipBridge.Windows;

public sealed class ClipboardSyncService
{
    private const int Port = 45837;
    private readonly string _code; private readonly Action<string> _status;
    private readonly string _deviceId = Guid.NewGuid().ToString();
    private readonly RecentMessageIds _seen = new();
    private readonly BoundedMemoryQueue<SignedClip> _pending = new(packet => packet.Message.Text.Length * 2L + 1024);
    private readonly Dictionary<string, TcpClient> _receiving = new();
    private volatile string? _lastSentId;
    private long _retryAtTicks;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = [];
    private readonly HashSet<TcpClient> _verifiedClients = [];
    private readonly Channel<OutboundMessage> _controls = Channel.CreateBounded<OutboundMessage>(128);
    private readonly Channel<byte> _sendSignal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly ClipboardActor _clipboardActor;
    private TcpClient? _activeClient;
    private volatile string? _lastHash;
    private Task? _connectTask;
    private readonly Task _sendTask;
    private readonly object _stopGate = new();
    private Task? _stopTask;
    public ClipboardSyncService(string code, Action<string> status, string? storageDirectory = null)
    {
        _code = code;
        _status = status;
        var storage = storageDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipBridge");
        _clipboardActor = new ClipboardActor(OnLocalClipboardRead, OnClipboardWriteCompleted, storage);
        _sendTask = Task.Run(SendLoopAsync);
    }

    public Task StartAsync(IPAddress phoneAddress)
    {
        _status($"正在连接手机 {phoneAddress}:{Port}…");
        _connectTask = ConnectLoopAsync(phoneAddress);
        return Task.CompletedTask;
    }
    public Task StopAsync()
    {
        lock (_stopGate) return _stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        _cts.Cancel();
        _controls.Writer.TryComplete();
        _sendSignal.Writer.TryComplete();
        TcpClient[] clients;
        lock (_clients)
        {
            clients = _clients.ToArray();
            _clients.Clear();
            _verifiedClients.Clear();
            _activeClient = null;
            _receiving.Clear();
        }
        foreach (var client in clients) client.Dispose();
        if (_connectTask is not null) await _connectTask;
        await _sendTask;
        _clipboardActor.Dispose();
        while (_controls.Reader.TryRead(out _)) { }
        _lastHash = null;
        var released = _pending.Bytes;
        _pending.Clear();
        TransferMemory.Released(released);
    }
    public void NotifyClipboardChanged()
    {
        if (_cts.IsCancellationRequested) return;
        if (_pending.CanAccept(1024)) _clipboardActor.RequestRead();
        else _status("发送队列已满，请等待对端确认后重新复制。");
    }

    private async Task ConnectLoopAsync(IPAddress phoneAddress)
    {
        while (!_cts.IsCancellationRequested)
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(phoneAddress, Port, timeout.Token);
                _cts.Token.ThrowIfCancellationRequested();
                AddClient(client);
                await ReadLoopAsync(client);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException)
            {
                _status($"暂时无法连接手机 {phoneAddress}，将自动重试。请确认手机已启动同步、IP 正确且位于同一局域网。");
            }
            try { await Task.Delay(2500, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
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
        _lastSentId = null;
        _status("网络已连接，正在验证配对码…");
        QueueSend(client, new Hello(_deviceId, Environment.MachineName, Proof($"hello|{_deviceId}|1")));
    }
    private async Task ReadLoopAsync(TcpClient client)
    {
        try
        {
            // StreamReader.ReadLineAsync and Deserialize(string) can leave giant
            // char/UTF-8 arrays in shared pools. Keep only an owned byte buffer.
            var reader = new JsonLineReader(client.GetStream());
            while (!_cts.IsCancellationRequested)
            {
                bool verified;
                lock (_clients) verified = _verifiedClients.Contains(client);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(verified ? 120 : 8));
                var line = await reader.ReadAsync(verified ? 512 * 1024 * 1024 : 64 * 1024, timeout.Token);
                if (line is null) break;
                await HandleAsync(line.Value, client);
                line = null;
            }
        }
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
            if (wasActive && !_cts.IsCancellationRequested) _status("手机已断开，正在自动重连；若手机换网后 IP 改变，请停止同步并更新手机 IP。");
        }
    }
    private void OnLocalClipboardRead(string text)
    {
        var fingerprint = PayloadIdentity.Hash(text);
        if (fingerprint == _lastHash || _cts.IsCancellationRequested) return;
        var message = new Clip(Guid.NewGuid().ToString(), _deviceId, text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var mac = PayloadIdentity.Mac(_code, $"clip|{message.Id}|{_deviceId}|", text, $"|{message.SentAt}");
        if (!_pending.TryAdd(new SignedClip(message, mac)))
        {
            _status("发送队列已满，请等待发送完成后重新复制。大图片单独发送，避免积压内存。");
            return;
        }
        _lastHash = fingerprint;
        _sendSignal.Writer.TryWrite(0);
        _status("已加入内存发送队列，连接并配对后自动发送。");
    }
    private Task HandleAsync(ReadOnlyMemory<byte> line, TcpClient client)
    {
        using var activity = line.Length >= TransferMemory.LargeTransferBytes ? TransferMemory.BeginActivity() : null;
        try
        {
            // Parse once: JsonDocument + Deserialize duplicated the full UTF-8 image.
            var packet = JsonSerializer.Deserialize<IncomingPacket>(line.Span);
            if (packet is null) return Task.CompletedTask;
            var type = packet.Type;
            if (type == "ping") { QueueSend(client, new ControlMessage("pong")); return Task.CompletedTask; }
            if (type == "hello")
            {
                var hello = packet;
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
                _sendSignal.Writer.TryWrite(0);
                _status($"已连接并通过配对验证：{hello.DeviceName}。内存中的待发送内容将自动恢复。");
                return Task.CompletedTask;
            }
            lock (_clients) { if (!_verifiedClients.Contains(client)) return Task.CompletedTask; }
            if (type == "ack")
            {
                var ack = packet;
                if (ack is null || ack.Proof != Proof($"ack|{ack.Id}|{(ack.Success ? "true" : "false")}")) return Task.CompletedTask;
                var pending = _pending.Peek();
                if (pending is null || pending.Message.Id != ack.Id) return Task.CompletedTask;
                if (ack.Success)
                {
                    _pending.Complete(pending);
                    TransferMemory.Released(pending.Message.Text.Length * 2L);
                    _status("手机已确认接收，发送队列已移除该项。");
                }
                else
                {
                    Interlocked.Exchange(ref _retryAtTicks, DateTime.UtcNow.AddSeconds(30).Ticks);
                    _status("手机暂时无法写入剪贴板，内容仍在内存中，稍后自动重试。");
                }
                _sendSignal.Writer.TryWrite(0);
                return Task.CompletedTask;
            }
            var signed = packet;
            if (signed.Type != "clip" || signed.Message is null || signed.Message.OriginDeviceId == _deviceId ||
                signed.Mac != PayloadIdentity.Mac(_code, $"clip|{signed.Message.Id}|{signed.Message.OriginDeviceId}|", signed.Message.Text, $"|{signed.Message.SentAt}"))
                return Task.CompletedTask;
            var id = signed.Message.Id;
            if (_seen.Contains(id)) { SendAcknowledgement(client, id, true); return Task.CompletedTask; }
            lock (_clients)
            {
                if (_receiving.ContainsKey(id)) { _receiving[id] = client; return Task.CompletedTask; }
                _receiving[id] = client;
            }
            _lastHash = PayloadIdentity.Hash(signed.Message.Text);
            if (!_clipboardActor.Enqueue(signed.Message.Text, result => OnReceptionCompleted(id, result)))
            {
                lock (_clients) _receiving.Remove(id);
                SendAcknowledgement(client, id, false);
            }
        }
        catch (Exception exception) { _status($"剪贴板处理失败：{exception.Message}"); }
        finally { TransferMemory.Released(line.Length); }
        return Task.CompletedTask;
    }
    private void QueueSend<T>(TcpClient client, T value)
    {
        if (!_controls.Writer.TryWrite(new OutboundMessage(client, JsonSerializer.Serialize(value) + "\n")))
            client.Dispose();
        _sendSignal.Writer.TryWrite(0);
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var _ in _sendSignal.Reader.ReadAllAsync(_cts.Token))
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (_controls.Reader.TryRead(out var control))
                    {
                        lock (_clients) if (!_clients.Contains(control.Client)) continue;
                        try { await control.Client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(control.Payload), _cts.Token); }
                        catch { control.Client.Dispose(); }
                        continue;
                    }
                    TcpClient? client;
                    lock (_clients) client = _activeClient is not null && _verifiedClients.Contains(_activeClient) ? _activeClient : null;
                    if (client is null) break;
                    var entry = _pending.Peek();
                    if (entry is null || (entry.Message.Id == _lastSentId && DateTime.UtcNow.Ticks < Interlocked.Read(ref _retryAtTicks))) break;
                    try
                    {
                        _lastSentId = entry.Message.Id;
                        Interlocked.Exchange(ref _retryAtTicks, DateTime.UtcNow.AddSeconds(120).Ticks);
                        await SendClipAsync(client, entry);
                        break; // Keep the item until the authenticated receiver ACKs it.
                    }
                    catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                    {
                        client.Dispose();
                        _lastSentId = null;
                        if (!_cts.IsCancellationRequested) _status("发送中断，内容保留在内存中，重连后重试。");
                        break;
                    }
                    finally { entry = null; }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task SendClipAsync(TcpClient client, SignedClip entry)
    {
        using var activity = TransferMemory.BeginActivity();
        try
        {
            var clip = entry.Message;
            await ClipTransport.WriteAsync(client.GetStream(), clip.Id, clip.OriginDeviceId, clip.Text, clip.SentAt, entry.Mac, _cts.Token);
        }
        finally { TransferMemory.Released(entry.Message.Text.Length * 2L); }
    }
    private void SendAcknowledgement(TcpClient client, string id, bool success) =>
        QueueSend(client, new Acknowledgement(id, success, Proof($"ack|{id}|{(success ? "true" : "false")}")));

    private void OnReceptionCompleted(string id, ClipboardWriteResult result)
    {
        TcpClient? client;
        lock (_clients)
        {
            _receiving.TryGetValue(id, out client);
            if (result.Success) _receiving.Remove(id);
        }
        if (result.Success) _seen.Add(id);
        if (client is not null) SendAcknowledgement(client, id, result.Success);
    }
    private void OnClipboardWriteCompleted(ClipboardWriteResult result) =>
        _status(result.Success
            ? $"已写入 Windows 剪贴板（{result.TextLength} 个字符，{result.ElapsedMilliseconds} ms，重试 {result.Attempts - 1} 次）。"
            : $"已收到 Android 内容，但 Windows 剪贴板持续被占用（{result.ElapsedMilliseconds} ms）：{result.Error}");
    private string Proof(string value) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_code), Encoding.UTF8.GetBytes(value)));
    private record Hello(string DeviceId, string DeviceName, string Proof) { public string Type => "hello"; public int Version => 1; }
    private record ControlMessage(string Type);
    private record Acknowledgement(string Id, bool Success, string Proof) { public string Type => "ack"; }
    private record OutboundMessage(TcpClient Client, string Payload);
    private record Clip(string Id, string OriginDeviceId, string Text, long SentAt) { [JsonIgnore] public string Canonical => $"clip|{Id}|{OriginDeviceId}|{Text}|{SentAt}"; }
    private record SignedClip(Clip Message, string Mac) { public string Type => "clip"; }
    private record IncomingPacket(string? Type, string? DeviceId, string? DeviceName, string? Proof,
        string? Id, bool Success, Clip? Message, string? Mac);
}
