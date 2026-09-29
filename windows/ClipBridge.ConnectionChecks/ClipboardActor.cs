using System.Collections.Concurrent;

namespace ClipBridge.Windows;

// Keep protocol tests away from the user's real clipboard.
internal sealed class ClipboardActor(Action<string> read, Action<ClipboardWriteResult> written, string? storageDirectory = null) : IDisposable
{
    internal static readonly ConcurrentQueue<string> Received = new();
    internal static string LocalText = "Windows test text";
    public void RequestRead() => read(LocalText);
    public bool Enqueue(string text, Action<ClipboardWriteResult>? completion = null)
    {
        Received.Enqueue(text);
        var result = new ClipboardWriteResult(true, text.Length, 0, 1, null);
        written(result);
        completion?.Invoke(result);
        return true;
    }
    public void Dispose() { _ = storageDirectory; }
}

internal sealed record ClipboardWriteResult(bool Success, int TextLength, long ElapsedMilliseconds, int Attempts, string? Error);
