using System.IO;

namespace ClipBridge.Windows;

internal sealed class ClipboardFileCache(string directory, long maxBytes = 256L * 1024 * 1024, int maxFiles = 50)
{
    internal const long MaxBytes = 256L * 1024 * 1024;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    internal string Store(byte[] bytes, string extension, string key)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"remote-{key}.{extension}");
        if (!File.Exists(path))
        {
            var temporary = path + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, path);
            }
            finally { TryDelete(temporary); }
        }
        return path;
    }

    internal void Cleanup(IEnumerable<string> protectedPaths)
    {
        if (!Directory.Exists(directory)) return;
        var pinned = protectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(directory, "remote-*")
            .Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        long retainedBytes = 0;
        var retainedCount = 0;
        foreach (var file in files.Where(f => pinned.Contains(f.FullName)))
        { retainedBytes += file.Length; retainedCount++; }
        foreach (var file in files.Where(f => !pinned.Contains(f.FullName)))
        {
            if (file.Extension == ".tmp" || DateTime.UtcNow - file.LastWriteTimeUtc > Retention
                || retainedCount >= maxFiles || retainedBytes + file.Length > maxBytes)
                TryDelete(file.FullName);
            else { retainedCount++; retainedBytes += file.Length; }
        }
    }
    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
