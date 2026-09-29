using System.Security.Cryptography;
using System.Text;

namespace ClipBridge.Windows;

internal static class PayloadIdentity
{
    internal static string Hash(string text)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, text);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string Mac(string code, params string[] parts)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(code));
        foreach (var part in parts) Append(hash, part);
        return Convert.ToBase64String(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string text)
    {
        var encoder = Encoding.UTF8.GetEncoder();
        Span<byte> bytes = stackalloc byte[4096];
        var remaining = text.AsSpan();
        do
        {
            encoder.Convert(remaining, bytes, true, out var charsUsed, out var bytesUsed, out _);
            hash.AppendData(bytes[..bytesUsed]);
            remaining = remaining[charsUsed..];
        } while (!remaining.IsEmpty);
    }
}
