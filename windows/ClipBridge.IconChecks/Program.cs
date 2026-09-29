using System.Drawing;
using System.Runtime.InteropServices;
using ClipBridge.Windows;

using var resource = typeof(TrayIconImage).Assembly.GetManifestResourceStream("ClipBridge.TrayIcon.ico")!;
using var buffer = new MemoryStream();
resource.CopyTo(buffer);
var bytes = buffer.ToArray();
foreach (var dpi in new uint[] { 96, 120, 144, 168, 192, 240, 288, 384 })
{
    var size = new Size(GetSystemMetricsForDpi(49, dpi), GetSystemMetricsForDpi(50, dpi));
    using var icon = TrayIconImage.Load(size);
    using var bitmap = icon.ToBitmap();
    if (icon.Size != size)
        throw new Exception($"DPI {dpi}: incorrect icon size {icon.Size} for {size}");
    Console.WriteLine($"{dpi / 96.0:P0}: requested {size.Width}px, loaded {icon.Width}px");
}

var frames = BitConverter.ToUInt16(bytes, 4);
for (var i = 0; i < frames; i++)
{
    var entry = 6 + i * 16;
    var width = bytes[entry] == 0 ? 256 : bytes[entry];
    var offset = (int)BitConverter.ToUInt32(bytes, entry + 12);
    var length = (int)BitConverter.ToUInt32(bytes, entry + 8);
    using var png = new MemoryStream(bytes, offset, length);
    using var expected = new Bitmap(png);
    using var icon = TrayIconImage.Load(new Size(width, width));
    using var actual = icon.ToBitmap();
    if (actual.Size != expected.Size) throw new Exception($"Incorrect frame: requested {width}, actual {actual.Size}, expected {expected.Size}, icon {icon.Size}");
    for (var y = 0; y < width; y++)
    for (var x = 0; x < width; x++)
    {
        var a = actual.GetPixel(x, y);
        var e = expected.GetPixel(x, y);
        if (a.A != e.A || (a.A != 0 && a.ToArgb() != e.ToArgb()))
            throw new Exception($"Frame {width}: pixel mismatch at {x},{y}");
    }
    Console.WriteLine($"{width}px: matches original ICO frame, pixel for pixel");
}
Console.WriteLine($"Current taskbar target: {TrayIconImage.GetSize()}");

[DllImport("user32.dll")]
static extern int GetSystemMetricsForDpi(int index, uint dpi);
