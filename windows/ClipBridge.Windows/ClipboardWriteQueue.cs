using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ClipBridge.Windows;

internal sealed class ClipboardActor : IDisposable
{
    private const string OriginMarkerFormat = "ClipBridge.Windows.OriginMarker.v1";
    private const string ImagePrefix = "clipbridge:png:";
    private const string JpegPrefix = "clipbridge:jpeg:";
    private const string GifPrefix = "clipbridge:gif:";
    private const string RawImagePrefix = "clipbridge:image:";
    private const int MaxImageBytes = 20 * 1024 * 1024;
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private readonly Action<string> _textRead;
    private readonly Action<ClipboardWriteResult> _writeCompleted;
    private readonly string _originId = Guid.NewGuid().ToString("N");
    private readonly Queue<PendingWrite> _pendingWrites = [];
    private bool _readRequested;
    private long _nextSequence;
    private string? _lastSelfMarker;
    private string? _lastSelfText;
    private uint _lastSelfClipboardSequence;
    private bool _stopping;

    public ClipboardActor(Action<string> textRead, Action<ClipboardWriteResult> writeCompleted)
    {
        _textRead = textRead;
        _writeCompleted = writeCompleted;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ClipBridge Clipboard Writer"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Enqueue(string text)
    {
        lock (_gate)
        {
            if (_stopping) return;
            if (_pendingWrites.Count == MaxPendingWrites) _pendingWrites.Dequeue();
            _pendingWrites.Enqueue(new PendingWrite(++_nextSequence, text));
        }
        _signal.Set();
    }

    public void RequestRead()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _readRequested = true;
        }
        _signal.Set();
    }

    private void Run()
    {
        while (true)
        {
            _signal.WaitOne();
            lock (_gate) { if (_stopping) return; }

            while (TryTakeWork(out var pending, out var shouldRead))
            {
                if (pending is not null)
                {
                    _writeCompleted(TryWrite(pending));
                }
                else if (shouldRead) TryRead();
            }
        }
    }

    private bool TryTakeWork(out PendingWrite? pending, out bool shouldRead)
    {
        lock (_gate)
        {
            if (_pendingWrites.Count > 0)
            {
                pending = _pendingWrites.Dequeue();
                shouldRead = false;
                return true;
            }
            if (_readRequested)
            {
                _readRequested = false;
                pending = null;
                shouldRead = true;
                return true;
            }
            pending = null;
            shouldRead = false;
            return false;
        }
    }

    private ClipboardWriteResult TryWrite(PendingWrite pending)
    {
        var stopwatch = Stopwatch.StartNew();
        string? lastError = null;
        for (var attempt = 1; attempt <= 200; attempt++)
        {
            var marker = $"{_originId}:{pending.Sequence}";
            var written = pending.Text.StartsWith(RawImagePrefix, StringComparison.Ordinal)
                ? TrySetClipboardRawImage(pending.Text, marker, out lastError)
                : pending.Text.StartsWith(GifPrefix, StringComparison.Ordinal)
                ? TrySetClipboardGif(pending.Text, marker, out lastError)
                : pending.Text.StartsWith(ImagePrefix, StringComparison.Ordinal) ||
                  pending.Text.StartsWith(JpegPrefix, StringComparison.Ordinal)
                    ? TrySetClipboardImage(pending.Text, out lastError)
                    : TrySetClipboardText(pending.Text, marker, out lastError);
            if (written)
            {
                _lastSelfMarker = marker;
                _lastSelfText = pending.Text;
                _lastSelfClipboardSequence = GetClipboardSequenceNumber();
                return new ClipboardWriteResult(pending.Text, true, attempt, null, stopwatch.ElapsedMilliseconds);
            }
            Thread.Sleep(attempt <= 50 ? 2 : 5);
        }
        return new ClipboardWriteResult(pending.Text, false, 200, lastError, stopwatch.ElapsedMilliseconds);
    }

    private static bool TrySetClipboardGif(string payload, string marker, out string? error)
    {
        try
        {
            var bytes = Convert.FromBase64String(payload[GifPrefix.Length..]);
            if (bytes.Length > MaxImageBytes) { error = "GIF 超过 20 MB"; return false; }
            return TrySetClipboardFile(bytes, "gif", marker, out error);
        }
        catch (Exception exception) { error = exception.Message; return false; }
    }

    private static bool TrySetClipboardRawImage(string payload, string marker, out string? error)
    {
        try
        {
            var separator = payload.IndexOf(':', RawImagePrefix.Length);
            if (separator <= RawImagePrefix.Length)
            {
                error = "图片载荷缺少格式";
                return false;
            }
            var extension = payload[RawImagePrefix.Length..separator].ToLowerInvariant();
            if (!IsSupportedImageExtension(extension))
            {
                error = "不支持的图片格式";
                return false;
            }
            var bytes = Convert.FromBase64String(payload[(separator + 1)..]);
            if (bytes.Length > MaxImageBytes)
            {
                error = "图片超过 20 MB";
                return false;
            }
            return TrySetClipboardFile(bytes, extension, marker, out error);
        }
        catch (Exception exception) { error = exception.Message; return false; }
    }

    private static bool TrySetClipboardFile(byte[] bytes, string extension, string marker, out string? error)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClipBridge",
                "clipboard"
            );
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"remote-{Guid.NewGuid():N}.{extension}");
            File.WriteAllBytes(path, bytes);
            var files = new System.Collections.Specialized.StringCollection { path };
            var data = new System.Windows.DataObject();
            data.SetFileDropList(files);
            data.SetData(OriginMarkerFormat, marker);
            System.Windows.Clipboard.SetDataObject(data, true);
            error = null;
            return true;
        }
        catch (Exception exception) { error = exception.Message; return false; }
    }

    private static bool TrySetClipboardImage(string payload, out string? error)
    {
        try
        {
            var prefix = payload.StartsWith(JpegPrefix, StringComparison.Ordinal) ? JpegPrefix : ImagePrefix;
            var bytes = Convert.FromBase64String(payload[prefix.Length..]);
            if (bytes.Length > MaxImageBytes) { error = "图片超过 20 MB"; return false; }
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            System.Windows.Clipboard.SetImage(image);
            error = null;
            return true;
        }
        catch (Exception exception) { error = exception.Message; return false; }
    }

    // Writing text through the native clipboard API avoids WPF/OLE's synchronous
    // clipboard flush, which can stall while the Windows history service is reading.
    private static bool TrySetClipboardText(string text, string marker, out string? error)
    {
        var textHandle = CreateGlobalText(text);
        var markerHandle = CreateGlobalText(marker);
        if (textHandle == IntPtr.Zero)
        {
            error = "无法分配剪贴板内存";
            if (markerHandle != IntPtr.Zero) GlobalFree(markerHandle);
            return false;
        }

        var opened = false;
        try
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }
            opened = true;
            if (!EmptyClipboard())
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }
            if (SetClipboardData(CfUnicodeText, textHandle) == IntPtr.Zero)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }
            textHandle = IntPtr.Zero; // Clipboard now owns this HGLOBAL.

            // The marker is only loop prevention metadata. Text has already been
            // written successfully, so a marker failure must not delay the sync.
            if (markerHandle != IntPtr.Zero &&
                SetClipboardData(RegisterClipboardFormat(OriginMarkerFormat), markerHandle) != IntPtr.Zero)
                markerHandle = IntPtr.Zero;

            error = null;
            return true;
        }
        finally
        {
            if (opened) CloseClipboard();
            if (textHandle != IntPtr.Zero) GlobalFree(textHandle);
            if (markerHandle != IntPtr.Zero) GlobalFree(markerHandle);
        }
    }

    private static IntPtr CreateGlobalText(string text)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(text + '\0');
        var handle = GlobalAlloc(GmemMoveable, (nuint)bytes.Length);
        if (handle == IntPtr.Zero) return IntPtr.Zero;
        var destination = GlobalLock(handle);
        if (destination == IntPtr.Zero)
        {
            GlobalFree(handle);
            return IntPtr.Zero;
        }
        try { Marshal.Copy(bytes, 0, destination, bytes.Length); }
        finally { GlobalUnlock(handle); }
        return handle;
    }

    private void TryRead()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var sequenceBefore = GetClipboardSequenceNumber();
                var dataObject = System.Windows.Clipboard.GetDataObject();
                var marker = dataObject?.GetDataPresent(OriginMarkerFormat, false) == true
                    ? dataObject.GetData(OriginMarkerFormat, false) as string
                    : null;
                var text = dataObject?.GetDataPresent(System.Windows.DataFormats.UnicodeText, true) == true
                    ? dataObject.GetData(System.Windows.DataFormats.UnicodeText, true) as string
                    : null;
                var sequenceAfter = GetClipboardSequenceNumber();

                // The clipboard changed while it was being read. Retry so the marker,
                // text, and sequence number always describe the same clipboard version.
                if (sequenceBefore != 0 && sequenceAfter != 0 && sequenceBefore != sequenceAfter)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // Suppress only the exact clipboard version written by this actor.
                // If the user later restores the same item from clipboard history,
                // Windows gives it a new sequence number and it is treated as local input.
                if (sequenceAfter != 0 && sequenceAfter == _lastSelfClipboardSequence)
                    return;

                _lastSelfMarker = null;
                _lastSelfText = null;
                _lastSelfClipboardSequence = 0;
                if (System.Windows.Clipboard.ContainsImage() &&
                    System.Windows.Clipboard.GetImage() is BitmapSource image)
                {
                    SendImage(image);
                }
                else if (dataObject?.GetDataPresent("PNG", true) == true)
                {
                    var pngData = dataObject.GetData("PNG", true);
                    var bytes = pngData switch
                    {
                        MemoryStream stream => stream.ToArray(),
                        byte[] array => array,
                        _ => null
                    };
                    if (bytes is { Length: <= MaxImageBytes })
                        _textRead(ImagePrefix + Convert.ToBase64String(bytes));
                }
                else if (dataObject?.GetDataPresent(System.Windows.DataFormats.FileDrop, true) == true &&
                         dataObject.GetData(System.Windows.DataFormats.FileDrop, true) is string[] files &&
                         files.Length == 1 && File.Exists(files[0]) && IsSupportedImageFile(files[0]))
                {
                    var extension = Path.GetExtension(files[0]).TrimStart('.').ToLowerInvariant();
                    var bytes = File.ReadAllBytes(files[0]);
                    if (bytes.Length <= MaxImageBytes)
                    {
                        if (extension == "gif")
                            _textRead(GifPrefix + Convert.ToBase64String(bytes));
                        else if (extension == "png")
                            _textRead(ImagePrefix + Convert.ToBase64String(bytes));
                        else if (extension is "jpg" or "jpeg" or "jfif")
                            _textRead(JpegPrefix + Convert.ToBase64String(bytes));
                        else
                            _textRead($"{RawImagePrefix}{extension}:{Convert.ToBase64String(bytes)}");
                    }
                }
                else if (text is not null) _textRead(text);
                return;
            }
            catch { Thread.Sleep(10); }
        }
    }

    private void SendImage(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var png = new MemoryStream();
        encoder.Save(png);
        if (png.Length <= MaxImageBytes)
        {
            _textRead(ImagePrefix + Convert.ToBase64String(png.ToArray()));
            return;
        }
        foreach (var quality in new[] { 92, 85, 75, 65, 55, 45 })
        {
            var jpegEncoder = new JpegBitmapEncoder { QualityLevel = quality };
            jpegEncoder.Frames.Add(BitmapFrame.Create(image));
            using var jpeg = new MemoryStream();
            jpegEncoder.Save(jpeg);
            if (jpeg.Length <= MaxImageBytes)
            {
                _textRead(JpegPrefix + Convert.ToBase64String(jpeg.ToArray()));
                return;
            }
        }
    }

    private static bool IsSupportedImageFile(string path) =>
        IsSupportedImageExtension(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    private static bool IsSupportedImageExtension(string extension) =>
        extension is "png" or "jpg" or "jpeg" or "jfif" or "bmp" or "gif" or
            "tif" or "tiff" or "webp" or "heic" or "heif" or "avif" or "ico";

    public void Dispose()
    {
        lock (_gate) { _stopping = true; _pendingWrites.Clear(); _readRequested = false; }
        _signal.Set();
        if (Thread.CurrentThread != _thread && _thread.Join(3000)) _signal.Dispose();
    }

    private sealed record PendingWrite(long Sequence, string Text);

    private const int MaxPendingWrites = 20;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}

internal sealed record ClipboardWriteResult(string Text, bool Success, int Attempts, string? Error, long ElapsedMilliseconds);
