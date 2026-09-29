using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipBridge.Windows;

const string code = "test-pairing-code";
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var listener = new TcpListener(IPAddress.Loopback, 45837);
var storage = Path.Combine(Path.GetTempPath(), "ClipBridge-connection-check-" + Guid.NewGuid().ToString("N"));
var service = new ClipboardSyncService(code, Console.WriteLine, storage);
string? unacknowledgedId = null;
try
{
    // The phone can start listening after Windows has already started retrying.
    await service.StartAsync(IPAddress.Loopback);
    await Task.Delay(200, deadline.Token);
    listener.Start();
    using (var phone = await listener.AcceptTcpClientAsync(deadline.Token))
    {
        using var reader = new StreamReader(phone.GetStream(), new UTF8Encoding(false));
        await CheckHello(reader);
        await Send(phone, Clip("before-auth", "must not apply"));
        await Send(phone, new { Type = "hello", Version = 1, DeviceId = "phone", DeviceName = "Test phone", Proof = Proof("hello|phone|1") });
        await Send(phone, Clip("received-1", "来自手机 📋"));
        await Send(phone, Clip("received-1", "来自手机 📋"));
        var invalid = new { Type = "clip", Message = new { Id = "bad", OriginDeviceId = "phone", Text = "must not apply", SentAt = 1L }, Mac = "invalid" };
        await Send(phone, invalid);
        await Send(phone, new { Type = "ping" });
        using var ack = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
        Assert(ack.RootElement.GetProperty("Type").GetString() == "ack" && ack.RootElement.GetProperty("Success").GetBoolean(), "receipt ACK after clipboard write");
        using var repeatedAck = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
        Assert(repeatedAck.RootElement.GetProperty("Id").GetString() == "received-1", "duplicate receives another ACK");
        using var pong = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
        Assert(pong.RootElement.GetProperty("Type").GetString() == "pong", "heartbeat reply");
        Assert(ClipboardActor.Received.ToArray().SequenceEqual(new[] { "来自手机 📋" }), "authenticated receive, deduplication and MAC validation");

        service.NotifyClipboardChanged();
        using var clip = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
        var message = clip.RootElement.GetProperty("Message");
        Assert(message.GetProperty("Text").GetString() == ClipboardActor.LocalText, "Windows sends on outbound connection");
        Assert(!message.TryGetProperty("Canonical", out _), "image payload is not duplicated in JSON");
        var canonical = $"clip|{message.GetProperty("Id").GetString()}|{message.GetProperty("OriginDeviceId").GetString()}|{ClipboardActor.LocalText}|{message.GetProperty("SentAt").GetInt64()}";
        Assert(clip.RootElement.GetProperty("Mac").GetString() == Proof(canonical), "outbound MAC");
        // Leave it unacknowledged: reconnect must resend the exact same item.
        unacknowledgedId = message.GetProperty("Id").GetString()!;
    }

    using (var phone = await listener.AcceptTcpClientAsync(deadline.Token))
    {
        using var reader = new StreamReader(phone.GetStream(), new UTF8Encoding(false));
        await CheckHello(reader);
        await Send(phone, new { Type = "hello", Version = 1, DeviceId = "phone", DeviceName = "Reconnected", Proof = Proof("hello|phone|1") });
        using var repeated = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
        Assert(repeated.RootElement.GetProperty("Message").GetProperty("Id").GetString() == unacknowledgedId, "unacknowledged payload survives reconnect");
        await Send(phone, new { Type = "ack", Id = unacknowledgedId, Success = true, Proof = Proof($"ack|{unacknowledgedId}|true") });
        await Send(phone, new { Type = "ping" });
        using var pong = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
        Assert(pong.RootElement.GetProperty("Type").GetString() == "pong", "ACK releases item before next control message");
        var releasedPayload = EnqueueLargePayload(service);
        await AcknowledgeLargePayload(phone, reader);
        Assert(await WaitForReleasedPayload(releasedPayload), "ACKed large payload collected without another clipboard change");
    }

    // Connection loss causes a fresh handshake; a wrong pairing code is rejected.
    using (var phone = await listener.AcceptTcpClientAsync(deadline.Token))
    {
        using var reader = new StreamReader(phone.GetStream(), new UTF8Encoding(false));
        await CheckHello(reader);
        await Send(phone, new { Type = "hello", Version = 1, DeviceId = "phone", DeviceName = "Wrong code", Proof = "invalid" });
        Assert(await reader.ReadLineAsync(deadline.Token) == null, "wrong code closes connection");
    }
    using (var phone = await listener.AcceptTcpClientAsync(deadline.Token))
    {
        using var reader = new StreamReader(phone.GetStream(), new UTF8Encoding(false));
        await CheckHello(reader);
        await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert(await reader.ReadLineAsync(deadline.Token) == null, "stop closes pending handshake");
    }
    listener.Stop();
    var offline = new ClipboardSyncService(code, _ => { }, storage);
    await offline.StartAsync(IPAddress.Loopback);
    await offline.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    Console.WriteLine("PASS: delayed phone startup, bidirectional protocol, authentication, deduplication, heartbeat, reconnect and cancellation.");
}
finally
{
    listener.Stop();
    await service.StopAsync();
}

string Proof(string input) => Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(input)));
object Clip(string id, string text) => new { Type = "clip", Message = new { Id = id, OriginDeviceId = "phone", Text = text, SentAt = 1L }, Mac = Proof($"clip|{id}|phone|{text}|1") };
async Task Send(TcpClient phone, object packet) => await phone.GetStream().WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet) + "\n"), deadline.Token);
async Task CheckHello(StreamReader reader)
{
    using var hello = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
    var root = hello.RootElement;
    Assert(root.GetProperty("Type").GetString() == "hello", "Windows initiates handshake");
    Assert(root.GetProperty("Proof").GetString() == Proof($"hello|{root.GetProperty("DeviceId").GetString()}|1"), "hello MAC");
}
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
}

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
WeakReference EnqueueLargePayload(ClipboardSyncService target)
{
    var text = new string('A', 4 * 1024 * 1024);
    var reference = new WeakReference(text);
    ClipboardActor.LocalText = text;
    target.NotifyClipboardChanged();
    ClipboardActor.LocalText = "Windows test text";
    return reference;
}
async Task AcknowledgeLargePayload(TcpClient phone, StreamReader reader)
{
    using var packet = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
    var message = packet.RootElement.GetProperty("Message");
    Assert(message.GetProperty("Text").GetString()!.Length == 4 * 1024 * 1024, "large payload streaming round trip");
    var id = message.GetProperty("Id").GetString();
    await Send(phone, new { Type = "ack", Id = id, Success = true, Proof = Proof($"ack|{id}|true") });
    await Send(phone, new { Type = "ping" });
    using var pong = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
    Assert(pong.RootElement.GetProperty("Type").GetString() == "pong", "large ACK processed before idle reclamation");
}
async Task<bool> WaitForReleasedPayload(WeakReference payload)
{
    for (var i = 0; i < 60; i++)
    {
        if (!payload.IsAlive) return true;
        await Task.Delay(100, deadline.Token);
    }
    return false;
}
