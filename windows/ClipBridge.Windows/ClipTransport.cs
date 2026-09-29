using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ClipBridge.Windows;

internal static class ClipTransport
{
    // SerializeAsync still buffers a whole string token. Bound individual tokens
    // here while keeping the existing JSON-line protocol unchanged.
    internal static async Task WriteAsync(Stream stream, string id, string origin, string text, long sentAt, string mac, CancellationToken cancellationToken)
    {
        var header = "{\"Type\":\"clip\",\"Message\":{\"Id\":" + JsonSerializer.Serialize(id) +
            ",\"OriginDeviceId\":" + JsonSerializer.Serialize(origin) + ",\"Text\":\"";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(header), cancellationToken);
        var buffer = new byte[(int)Math.Clamp(text.Length * 6L, 1024, 64 * 1024)];
        var used = 0;
        for (var offset = 0; offset < text.Length;)
        {
            var length = Math.Min(4096, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]) && char.IsLowSurrogate(text[offset + length])) length--;
            if (used + length > buffer.Length)
            {
                await stream.WriteAsync(buffer.AsMemory(0, used), cancellationToken);
                used = 0;
            }
            if (CopySimpleAscii(text, offset, length, buffer, used)) used += length;
            else
            {
                var encoded = JsonEncodedText.Encode(text.AsSpan(offset, length), JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
                if (used + encoded.EncodedUtf8Bytes.Length > buffer.Length)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, used), cancellationToken);
                    used = 0;
                }
                encoded.EncodedUtf8Bytes.CopyTo(buffer.AsSpan(used));
                used += encoded.EncodedUtf8Bytes.Length;
            }
            offset += length;
        }
        if (used > 0) await stream.WriteAsync(buffer.AsMemory(0, used), cancellationToken);
        var tail = "\",\"SentAt\":" + sentAt.ToString(CultureInfo.InvariantCulture) + "},\"Mac\":" + JsonSerializer.Serialize(mac) + "}\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(tail), cancellationToken);
    }

    private static bool CopySimpleAscii(string text, int offset, int length, byte[] buffer, int used)
    {
        for (var i = 0; i < length; i++)
        {
            var character = text[offset + i];
            if (character < 0x20 || character > 0x7F || character is '"' or '\\') return false;
            buffer[used + i] = (byte)character;
        }
        return true;
    }
}
