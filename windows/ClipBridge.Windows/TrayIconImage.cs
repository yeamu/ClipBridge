using System.Drawing;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace ClipBridge.Windows;

internal static class TrayIconImage
{
    private const int SmCxSmallIcon = 49;
    private const int SmCySmallIcon = 50;

    internal static Size GetSize()
    {
        // The notification area belongs to the primary taskbar, which may be on
        // a different monitor (and DPI) from our settings window.
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var dpi = taskbar != IntPtr.Zero ? GetDpiForWindow(taskbar) : 0;
        if (dpi == 0) dpi = GetDpiForSystem();
        return new Size(GetSystemMetricsForDpi(SmCxSmallIcon, dpi), GetSystemMetricsForDpi(SmCySmallIcon, dpi));
    }

    internal static Icon Load(Size size)
    {
        // Select a frame from the original multi-resolution ICO. Extracting the
        // EXE's associated icon first discards the other resolutions.
        using var stream = typeof(TrayIconImage).Assembly
            .GetManifestResourceStream("ClipBridge.TrayIcon.ico")
            ?? throw new InvalidOperationException("Missing embedded tray icon.");
        // Icon(Stream, Size) can choose a smaller nearest frame (24px for a
        // 28px tray at 175%). Prefer the next larger frame so the native loader only
        // needs to downscale when the ICO has no exact match.
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        stream.Position = 4;
        var count = reader.ReadUInt16();
        var selected = Size.Empty;
        var largest = Size.Empty;
        var selectedEntry = 0;
        var largestEntry = 0;
        for (var i = 0; i < count; i++)
        {
            stream.Position = 6 + i * 16;
            var width = reader.ReadByte();
            var height = reader.ReadByte();
            var frame = new Size(width == 0 ? 256 : width, height == 0 ? 256 : height);
            if (frame.Width * frame.Height > largest.Width * largest.Height)
            {
                largest = frame;
                largestEntry = i;
            }
            if (frame.Width >= size.Width && frame.Height >= size.Height
                && (selected.IsEmpty || frame.Width * frame.Height < selected.Width * selected.Height))
            {
                selected = frame;
                selectedEntry = i;
            }
        }
        stream.Position = 6 + (selected.IsEmpty ? largestEntry : selectedEntry) * 16 + 8;
        var length = checked((int)reader.ReadUInt32());
        var offset = reader.ReadUInt32();
        stream.Position = offset;
        var data = reader.ReadBytes(length);
        var handle = CreateIconFromResourceEx(data, (uint)data.Length, true, 0x00030000,
            size.Width, size.Height, 0);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            // Clone owns its handle; the native temporary is always released.
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size,
        [MarshalAs(UnmanagedType.Bool)] bool isIcon, uint version, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
