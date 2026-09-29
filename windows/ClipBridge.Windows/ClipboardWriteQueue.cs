using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipBridge.Windows;

internal sealed class ClipboardActor : IDisposable
{
    private const string OriginMarkerFormat = "ClipBridge.Windows.OriginMarker.v1";
    private const string ImagePrefix = "clipbridge:png:";
    private const string JpegPrefix = "clipbridge:jpeg:";
    private const string GifPrefix = "clipbridge:gif:";
    private const string RawImagePrefix = "clipbridge:image:";
    private const int MaxImageBytes = 100 * 1024 * 1024;
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private readonly Action<string> _textRead;
    private readonly Action<ClipboardWriteResult> _writeCompleted;
    private readonly string _originId = Guid.NewGuid().ToString("N");
    private readonly BoundedMemoryQueue<PendingWrite> _pendingWrites = new(item => item.Text.Length * 2L + 1024);
    private readonly ClipboardFileCache _fileCache;
    private DateTime _retryAfter;
    private bool _readRequested;
    private uint _lastSelfClipboardSequence;
    private bool _stopping;

    public ClipboardActor(Action<string> textRead, Action<ClipboardWriteResult> writeCompleted, string? storageDirectory = null)
    {
        var storage = storageDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipBridge");
        _fileCache = new ClipboardFileCache(Path.Combine(storage, "clipboard"));
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

    public bool Enqueue(string text, Action<ClipboardWriteResult>? completion = null)
    {
        lock (_gate)
        {
            if (_stopping) return false;
            if (!_pendingWrites.TryAdd(new PendingWrite(Guid.NewGuid().ToString("N"), text, completion)))
            {
                _writeCompleted(new ClipboardWriteResult(0, false, 0, "接收队列已满，对端会保留内容并重试", 0));
                return false;
            }
            _signal.Set();
            return true;
        }
    }

    public void RequestRead()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _readRequested = true;
            _signal.Set();
        }
    }

    private void Run()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        try
        {
            CleanupCache();
            while (true)
            {
                ClipboardMessagePump.Drain();
                bool read;
                lock (_gate)
                {
                    if (_stopping) return;
                    read = _readRequested;
                    _readRequested = false;
                }
                if (read) TryRead();
                var entry = _pendingWrites.Peek();
                if (entry is not null && DateTime.UtcNow >= _retryAfter)
                {
                    try
                    {
                        var result = TryWrite(entry.Text, entry.Id);
                        if (result.Success)
                        {
                            _pendingWrites.Complete(entry);
                            TransferMemory.Released(entry.Text.Length * 2L);
                            _retryAfter = DateTime.MinValue;
                        }
                        else
                        {
                            _retryAfter = DateTime.UtcNow.AddSeconds(30);
                        }
                        _writeCompleted(result);
                        entry.Completion?.Invoke(result);
                    }
                    catch (Exception exception)
                    {
                        _retryAfter = DateTime.UtcNow.AddSeconds(30);
                        var result = new ClipboardWriteResult(0, false, 0, exception.Message, 0);
                        _writeCompleted(result);
                        entry.Completion?.Invoke(result);
                    }
                    entry = null;
                    continue;
                }
                var wait = entry is null ? Timeout.Infinite : Math.Clamp((int)(_retryAfter - DateTime.UtcNow).TotalMilliseconds, 10, 30000);
                entry = null;
                ClipboardMessagePump.Wait(_signal, wait);
            }
        }
        finally
        {
            var released = _pendingWrites.Bytes;
            lock (_gate) { _pendingWrites.Clear(); _signal.Dispose(); }
            ClipboardMessagePump.Drain();
            dispatcher.InvokeShutdown();
            TransferMemory.Released(released);
        }
    }

    private ClipboardWriteResult TryWrite(string text, string key)
    {
        using var activity = TransferMemory.BeginActivity();
        var stopwatch = Stopwatch.StartNew();
        var marker = $"{_originId}:{key}";
        string? path = null;
        BitmapSource? image = null;
        string? lastError = null;
        // Decode and create file-backed content ONCE, before clipboard retries.
        try
        {
            if (text.StartsWith(GifPrefix, StringComparison.Ordinal))
                path = PrepareFile(text, GifPrefix, "gif", key);
            else if (text.StartsWith(RawImagePrefix, StringComparison.Ordinal))
            {
                var separator = text.IndexOf(':', RawImagePrefix.Length);
                if (separator <= RawImagePrefix.Length) throw new InvalidDataException("图片载荷缺少格式");
                var extension = text[RawImagePrefix.Length..separator].ToLowerInvariant();
                if (!IsSupportedImageExtension(extension)) throw new InvalidDataException("不支持的图片格式");
                path = PrepareFile(text, text[..(separator + 1)], extension, key);
            }
            else if (text.StartsWith(ImagePrefix, StringComparison.Ordinal) || text.StartsWith(JpegPrefix, StringComparison.Ordinal))
                image = PrepareImage(text);
        }
        catch (Exception exception) { return new ClipboardWriteResult(text.Length, false, 0, exception.Message, stopwatch.ElapsedMilliseconds); }
        if (image is not null) TransferMemory.Released((long)image.PixelWidth * image.PixelHeight * 4);
        for (var attempt = 1; attempt <= 200; attempt++)
        {
            lock (_gate) if (_stopping) return new ClipboardWriteResult(text.Length, false, attempt, "已停止", stopwatch.ElapsedMilliseconds);
            var written = path is not null ? TrySetClipboardFile(path, marker, out lastError)
                : image is not null
                    ? TrySetClipboardImage(image, out lastError)
                    : TrySetClipboardText(text, marker, out lastError);
            if (written)
            {
                _lastSelfClipboardSequence = GetClipboardSequenceNumber();
                CleanupCache();
                return new ClipboardWriteResult(text.Length, true, attempt, null, stopwatch.ElapsedMilliseconds);
            }
            Thread.Sleep(attempt <= 50 ? 2 : 5);
        }
        return new ClipboardWriteResult(text.Length, false, 200, lastError + "；内容保留在内存中，每 30 秒重试", stopwatch.ElapsedMilliseconds);
    }

    private string PrepareFile(string payload, string prefix, string extension, string key)
    {
        if (payload.Length - prefix.Length > ((MaxImageBytes + 2L) / 3) * 4)
            throw new InvalidDataException("图片超过 100 MB");
        var bytes = PayloadCodec.Decode(payload.AsSpan(prefix.Length), MaxImageBytes);
        if (bytes.Length > MaxImageBytes) throw new InvalidDataException("图片超过 100 MB");
        var path = _fileCache.Store(bytes, extension, key);
        CleanupCache(path);
        return path;
    }

    private void CleanupCache(string? preparedPath = null)
    {
        try
        {
            var paths = CurrentFileReferences();
            if (preparedPath is not null) paths.Add(preparedPath);
            _fileCache.Cleanup(paths);
        }
        catch { } // Never prune when the current clipboard references cannot be read.
    }

    private static bool TrySetClipboardFile(string path, string marker, out string? error)
    {
        try
        {
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

    private static BitmapSource PrepareImage(string payload)
    {
        var prefix = payload.StartsWith(JpegPrefix, StringComparison.Ordinal) ? JpegPrefix : ImagePrefix;
        if (payload.Length - prefix.Length > ((MaxImageBytes + 2L) / 3) * 4) throw new InvalidDataException("图片超过 100 MB");
        var bytes = PayloadCodec.Decode(payload.AsSpan(prefix.Length), MaxImageBytes);
        if (bytes.Length > MaxImageBytes) throw new InvalidDataException("图片超过 100 MB");
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream;
        image.EndInit(); image.Freeze();
        return image;
    }

    private static bool TrySetClipboardImage(BitmapSource image, out string? error)
    {
        try { System.Windows.Clipboard.SetImage(image); error = null; return true; }
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
        using var activity = TransferMemory.BeginActivity();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                var sequenceBefore = GetClipboardSequenceNumber();
                if (sequenceBefore != 0 && sequenceBefore == _lastSelfClipboardSequence) return;
                var dataObject = System.Windows.Clipboard.GetDataObject();
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

                _lastSelfClipboardSequence = 0;
                // Use the source PNG when available: avoid decoding a full bitmap
                // and encoding it again for a screenshot already stored as PNG.
                if (dataObject?.GetDataPresent("PNG", false) == true)
                {
                    var pngData = dataObject.GetData("PNG", true);
                    var bytes = pngData switch
                    {
                        MemoryStream stream when stream.TryGetBuffer(out var buffer) => buffer.AsMemory(0, (int)stream.Length),
                        MemoryStream stream => stream.ToArray().AsMemory(),
                        byte[] array => array.AsMemory(),
                        _ => ReadOnlyMemory<byte>.Empty
                    };
                    if (bytes.Length > 0 && bytes.Length <= MaxImageBytes) PublishImage(ImagePrefix, bytes);
                    else if (System.Windows.Clipboard.GetImage() is BitmapSource fallback) SendImage(fallback);
                }
                else if (System.Windows.Clipboard.ContainsImage() &&
                    System.Windows.Clipboard.GetImage() is BitmapSource image)
                {
                    SendImage(image);
                }
                else if (dataObject?.GetDataPresent(System.Windows.DataFormats.FileDrop, true) == true &&
                         dataObject.GetData(System.Windows.DataFormats.FileDrop, true) is string[] files &&
                         files.Length == 1 && File.Exists(files[0]) && IsSupportedImageFile(files[0]))
                {
                    var extension = Path.GetExtension(files[0]).TrimStart('.').ToLowerInvariant();
                    if (new FileInfo(files[0]).Length > MaxImageBytes) return;
                    var bytes = File.ReadAllBytes(files[0]);
                    if (bytes.Length <= MaxImageBytes)
                    {
                        if (extension == "gif")
                            PublishImage(GifPrefix, bytes);
                        else if (extension == "png")
                            PublishImage(ImagePrefix, bytes);
                        else if (extension is "jpg" or "jpeg" or "jfif")
                            PublishImage(JpegPrefix, bytes);
                        else
                            PublishImage($"{RawImagePrefix}{extension}:", bytes);
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
        TransferMemory.Released((long)image.PixelWidth * image.PixelHeight * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var png = new MemoryStream();
        encoder.Save(png);
        if (png.Length <= MaxImageBytes)
        {
            PublishImage(ImagePrefix, png.GetBuffer().AsMemory(0, (int)png.Length));
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
                PublishImage(JpegPrefix, jpeg.GetBuffer().AsMemory(0, (int)jpeg.Length));
                return;
            }
        }
    }

    private static List<string> CurrentFileReferences()
    {
        const uint fileDrop = 15;
        if (!IsClipboardFormatAvailable(fileDrop)) return [];
        if (!OpenClipboard(IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var handle = GetClipboardData(fileDrop);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var count = DragQueryFile(handle, uint.MaxValue, null, 0);
            var paths = new List<string>();
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFile(handle, index, null, 0);
                var path = new System.Text.StringBuilder(checked((int)length + 1));
                DragQueryFile(handle, index, path, (uint)path.Capacity);
                paths.Add(path.ToString());
            }
            return paths;
        }
        finally { CloseClipboard(); }
    }
    private void PublishImage(string prefix, ReadOnlyMemory<byte> bytes)
    {
        _textRead(PayloadCodec.Encode(prefix, bytes));
        TransferMemory.Released(bytes.Length * 3L);
    }

    private static bool IsSupportedImageFile(string path) =>
        IsSupportedImageExtension(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    private static bool IsSupportedImageExtension(string extension) =>
        extension is "png" or "jpg" or "jpeg" or "jfif" or "bmp" or "gif" or
            "tif" or "tiff" or "webp" or "avif" or "ico";

    private sealed record PendingWrite(string Id, string Text, Action<ClipboardWriteResult>? Completion);

    public void Dispose()
    {
        long released;
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            _readRequested = false;
            released = _pendingWrites.Bytes;
            _pendingWrites.Clear();
            _signal.Set();
        }
        TransferMemory.Released(released);
        if (Thread.CurrentThread != _thread) _thread.Join(3000);
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint index, System.Text.StringBuilder? name, uint length);

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

internal sealed record ClipboardWriteResult(int TextLength, bool Success, int Attempts, string? Error, long ElapsedMilliseconds);
