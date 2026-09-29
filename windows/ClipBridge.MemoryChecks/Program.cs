using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClipBridge.Windows;

var queue = new BoundedMemoryQueue<byte[]>(a => a.Length, 3, 10, 30);
var first = new byte[6];
Assert(queue.TryAdd(first) && !queue.TryAdd(new byte[6]), "byte budget rejects new accumulation");
Assert(ReferenceEquals(queue.Peek(), first), "rejected item does not evict existing data");
Assert(!queue.Complete(new byte[6]) && queue.Complete(first), "only current head can be released");
var large = new byte[25];
Assert(queue.TryAdd(large) && !queue.TryAdd(new byte[1]), "one large item can travel alone");
queue.Clear();
Assert(queue.Peek() is null && !queue.TryAdd(new byte[31]), "clear releases references and enforces single-item limit");
Assert(queue.TryAdd(new byte[1]) && queue.TryAdd(new byte[1]) && queue.TryAdd(new byte[1]) && !queue.TryAdd(new byte[1]), "item-count limit");

var seen = new RecentMessageIds(3);
Assert(seen.Add("1") && !seen.Add("1"), "duplicate suppression");
seen.Add("2"); seen.Add("3"); seen.Add("4");
Assert(!seen.Contains("1") && seen.Contains("4"), "deduplication history is bounded");
var text = string.Concat(Enumerable.Repeat("中文 📋\n", 2000));
Assert(PayloadIdentity.Hash(text) == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), "chunked UTF-8 fingerprint");
var expected = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("code"), Encoding.UTF8.GetBytes("prefix|" + text + "|suffix")));
Assert(PayloadIdentity.Mac("code", "prefix|", text, "|suffix") == expected, "chunked HMAC matches wire protocol");

foreach (var length in new[] { 0, 1, 2, 3, 31, 4096 })
{
    var bytes = RandomNumberGenerator.GetBytes(length);
    var payload = PayloadCodec.Encode("clipbridge:png:", bytes);
    Assert(payload == "clipbridge:png:" + Convert.ToBase64String(bytes) &&
        PayloadCodec.Decode(payload.AsSpan("clipbridge:png:".Length), 8192).SequenceEqual(bytes), "Base64 round trip " + length);
}
Assert(PayloadCodec.Decode(" YQ== \r\n".AsSpan(), 100).SequenceEqual(new byte[] { 97 }), "Base64 whitespace compatibility");
var invalidRejected = false;
try { PayloadCodec.Decode("!!!!".AsSpan(), 100); } catch (FormatException) { invalidRejected = true; }
Assert(invalidRejected, "invalid Base64 rejected");
var imageLimitRejected = false;
try { PayloadCodec.Decode("AAAA".AsSpan(), 2); } catch (InvalidDataException) { imageLimitRejected = true; }
Assert(imageLimitRejected, "decoded byte limit checked before allocation");

var boundary = new string('x', 4095) + "📋中文\n\"\\\t\0<>+" + new string('y', 5000);
using (var stream = new MemoryStream())
{
    await ClipTransport.WriteAsync(stream, "id\"", "origin", boundary, 123, "mac", CancellationToken.None);
    using var packet = JsonDocument.Parse(stream.ToArray());
    Assert(packet.RootElement.GetProperty("Message").GetProperty("Text").GetString() == boundary,
        "streaming JSON preserves surrogate boundaries, control characters and escaping");
    Assert(stream.ToArray()[^1] == '\n', "streaming JSON line terminator");
}
// Measure the real serializer against a sink that never retains the wire data.
var largeText = new string('A', 12 * 1024 * 1024);
var before = GC.GetTotalAllocatedBytes(true);
await JsonSerializer.SerializeAsync(new CountingStream(), new { Type = "clip", Message = new { Id = "id", OriginDeviceId = "origin", Text = largeText, SentAt = 1 }, Mac = "mac" });
var oldAllocation = GC.GetTotalAllocatedBytes(true) - before;
before = GC.GetTotalAllocatedBytes(true);
var sink = new CountingStream();
await ClipTransport.WriteAsync(sink, "id", "origin", largeText, 1, "mac", CancellationToken.None);
var newAllocation = GC.GetTotalAllocatedBytes(true) - before;
Console.WriteLine($"Large JSON allocation: original={oldAllocation:N0}, streaming={newAllocation:N0}, max write={sink.MaxWrite:N0} bytes");
Assert(newAllocation < oldAllocation / 2 && sink.MaxWrite <= 64 * 1024, "large JSON has bounded writes and fewer allocations");

using (var lines = new MemoryStream(Encoding.UTF8.GetBytes("{\"Text\":\"中文📋\"}\r\n{}\nlast")))
{
    var reader = new JsonLineReader(lines);
    var firstLine = await reader.ReadAsync(1024, CancellationToken.None);
    Assert(Encoding.UTF8.GetString(firstLine!.Value.Span) == "{\"Text\":\"中文📋\"}", "UTF-8 byte reader preserves Unicode and strips CRLF");
    Assert(Encoding.UTF8.GetString((await reader.ReadAsync(1024, CancellationToken.None))!.Value.Span) == "{}", "byte reader keeps following packet");
    Assert(Encoding.UTF8.GetString((await reader.ReadAsync(1024, CancellationToken.None))!.Value.Span) == "last" &&
        await reader.ReadAsync(1024, CancellationToken.None) is null, "byte reader handles final packet and EOF");
}
using (var oversized = new MemoryStream(Encoding.UTF8.GetBytes("12345\n")))
{
    var rejected = false;
    try { await new JsonLineReader(oversized).ReadAsync(4, CancellationToken.None); }
    catch (InvalidDataException) { rejected = true; }
    Assert(rejected, "byte reader rejects oversized frames");
}

var directory = Path.Combine(Path.GetTempPath(), "ClipBridge-memory-check-" + Guid.NewGuid().ToString("N"));
var cache = new ClipboardFileCache(directory);
var path = cache.Store(new byte[16], "gif", "same-item");
for (var i = 0; i < 200; i++) AssertSame(cache.Store(new byte[16], "gif", "same-item"), path);
Assert(Directory.GetFiles(directory).Length == 1, "200 retries reuse one image file");
for (var i = 0; i < 70; i++) cache.Store(new byte[16], "gif", "item" + i);
cache.Cleanup(new[] { path });
Assert(File.Exists(path) && Directory.GetFiles(directory).Length <= 50, "cache count bound protects current clipboard file");
new ClipboardFileCache(directory, maxBytes: 32).Cleanup(new[] { path });
Assert(File.Exists(path) && Directory.GetFiles(directory).Sum(p => new FileInfo(p).Length) <= 32, "cache byte budget");
File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
var expired = cache.Store(new byte[1], "gif", "expired");
File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-8));
cache.Cleanup(new[] { path });
Assert(File.Exists(path) && !File.Exists(expired), "expiration preserves pinned references");
var collectionCount = TransferMemory.CollectionCount;
TransferMemory.Released(1024);
await Task.Delay(3100);
Assert(TransferMemory.CollectionCount == collectionCount, "small text does not force a full GC");
using (TransferMemory.BeginActivity())
{
    TransferMemory.Released(8L * 1024 * 1024);
    await Task.Delay(3100);
    Assert(TransferMemory.CollectionCount == collectionCount, "reclamation defers while a transfer is active");
}
Assert(SpinWait.SpinUntil(() => TransferMemory.CollectionCount > collectionCount, 5000), "large transfer triggers idle reclamation");
collectionCount = TransferMemory.CollectionCount;
using (var source = new MemoryStream())
{
    var bytes = new byte[5 * 1024 * 1024 + 1];
    Array.Fill(bytes, (byte)'a'); bytes[^1] = (byte)'\n';
    source.Write(bytes); source.Position = 0;
    var reader = new JsonLineReader(source);
    var packet = await reader.ReadAsync(8 * 1024 * 1024, CancellationToken.None);
    Assert(packet!.Value.Length == bytes.Length - 1 && packet.Value.Span[0] == 'a' && packet.Value.Span[^1] == 'a',
        "large byte frame crosses buffer boundaries without corruption");
    Assert(await reader.ReadAsync(8 * 1024 * 1024, CancellationToken.None) is null, "large frame followed by EOF");
}
Assert(SpinWait.SpinUntil(() => TransferMemory.CollectionCount > collectionCount, 5000), "large receive leaves reclamation activity balanced");
Console.WriteLine("PASS: memory queue limits, release, bounded IDs, hashes, retry reuse and cache lifetime.");

static void Assert(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS: " + label); }
static void AssertSame(string first, string second) { if (first != second) throw new Exception("retry created a new cache path"); }

sealed class CountingStream : Stream
{
    public int MaxWrite { get; private set; }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => 0;
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) => MaxWrite = Math.Max(MaxWrite, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { MaxWrite = Math.Max(MaxWrite, buffer.Length); return ValueTask.CompletedTask; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
