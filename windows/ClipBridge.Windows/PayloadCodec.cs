using System.IO;

namespace ClipBridge.Windows;

internal static class PayloadCodec
{
    internal static string Encode(string prefix, ReadOnlyMemory<byte> bytes) =>
        string.Create(checked(prefix.Length + ((bytes.Length + 2) / 3) * 4), (prefix, bytes), (destination, state) =>
        {
            state.prefix.AsSpan().CopyTo(destination);
            if (!Convert.TryToBase64Chars(state.bytes.Span, destination[state.prefix.Length..], out _))
                throw new InvalidOperationException("Base64 encoding failed");
        });

    internal static byte[] Decode(ReadOnlySpan<char> encoded, int maxBytes)
    {
        if (encoded.Length > ((maxBytes + 2L) / 3) * 4) throw new InvalidDataException("图片超过 100 MB");
        var characters = 0;
        var padding = 0;
        foreach (var character in encoded)
        {
            if (char.IsWhiteSpace(character)) continue;
            characters++;
            padding = character == '=' ? padding + 1 : 0;
        }
        if (characters % 4 != 0 || padding > 2) throw new FormatException("无效的 Base64 图片");
        var length = checked(characters / 4 * 3 - padding);
        if (length < 0 || length > maxBytes) throw new InvalidDataException("图片超过 100 MB");
        var bytes = new byte[length];
        if (!Convert.TryFromBase64Chars(encoded, bytes, out var written) || written != length)
            throw new FormatException("无效的 Base64 图片");
        return bytes;
    }
}
