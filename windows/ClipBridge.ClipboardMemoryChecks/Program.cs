using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipBridge.Windows;

// Exercise the real STA worker and WIC encoder without writing to the user's clipboard.
var payloads = new List<WeakReference<string>>();
var storage = Path.Combine(Path.GetTempPath(), "ClipBridge-STA-check-" + Guid.NewGuid().ToString("N"));
using var actor = new ClipboardActor(text => payloads.Add(new WeakReference<string>(text)), _ => { }, storage);
var thread = (Thread)typeof(ClipboardActor).GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(actor)!;
Dispatcher? dispatcher = null;
Assert(SpinWait.SpinUntil(() => (dispatcher = Dispatcher.FromThread(thread)) is not null, 3000), "STA dispatcher initialized");
var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
// FromThread publishes a dispatcher before its hidden HWND is fully initialized.
for (var attempt = 0; attempt < 30; attempt++)
{
    var posted = dispatcher!.BeginInvoke(DispatcherPriority.Normal, new Action(() => ready.TrySetResult()));
    if (posted.Status != DispatcherOperationStatus.Aborted) break;
    await Task.Delay(100);
}
await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(true, "idle STA worker pumps dispatcher messages");

var process = Process.GetCurrentProcess();
process.Refresh();
var baseline = process.PrivateMemorySize64;
var peak = baseline;
var encode = typeof(ClipboardActor).GetMethod("SendImage", BindingFlags.Instance | BindingFlags.NonPublic)!;
var previousIdle = 0L;
for (var batch = 0; batch < 2; batch++)
{
    var collections = TransferMemory.CollectionCount;
    for (var i = 0; i < 5; i++)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher!.BeginInvoke(() =>
        {
            try
            {
                using var activity = TransferMemory.BeginActivity();
                var pixels = RandomNumberGenerator.GetBytes(3840 * 2160 * 4);
                var bitmap = BitmapSource.Create(3840, 2160, 96, 96, PixelFormats.Bgra32, null, pixels, 3840 * 4);
                encode.Invoke(actor, new object[] { bitmap });
                completed.SetResult();
            }
            catch (Exception exception) { completed.SetException(exception); }
        });
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        process.Refresh(); peak = Math.Max(peak, process.PrivateMemorySize64);
    }
    Assert(payloads.Count == (batch + 1) * 5, "five full 4K images encoded by production path, batch " + (batch + 1));
    // No GC.Collect in this test: the application's idle reclaimer must do the work.
    Assert(SpinWait.SpinUntil(() => payloads.All(reference => !reference.TryGetTarget(out _)), 10000),
        "large image strings reclaimed automatically while idle");
    Assert(SpinWait.SpinUntil(() => TransferMemory.CollectionCount >= collections + 2, 5000), "finalizer cleanup followed by buffer reclamation");
    await Task.Delay(500);
    process.Refresh();
    var idle = process.PrivateMemorySize64;
    Console.WriteLine($"Batch {batch + 1} private memory: baseline={baseline / 1048576d:F1} MiB, peak={peak / 1048576d:F1} MiB, idle={idle / 1048576d:F1} MiB; live managed={GC.GetTotalMemory(false) / 1048576d:F1} MiB; last collection={TransferMemory.LastCollectionMilliseconds:F1} ms");
    Assert(idle < peak - 32L * 1024 * 1024 && GC.GetTotalMemory(false) < 8L * 1024 * 1024, "idle cleanup reduces process memory and leaves no large managed buffers");
    if (batch > 0) Assert(idle <= previousIdle + 32L * 1024 * 1024, "another batch does not accumulate native image memory");
    previousIdle = idle;
}
// Invalid image data fails before touching the system clipboard. Its raw payload
// must stay queued for retry, then stop must release it even without a new image.
var failure = new TaskCompletionSource<ClipboardWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
var failedPayload = EnqueueInvalidImage(actor, failure);
var failedWrite = await failure.Task.WaitAsync(TimeSpan.FromSeconds(3));
Assert(!failedWrite.Success && failedWrite.TextLength > 4 * 1024 * 1024, "failed image reports length without retaining its payload");
actor.Dispose();
Assert(SpinWait.SpinUntil(() => !failedPayload.IsAlive, 7000), "stopping releases a failed pending image automatically");
Console.WriteLine("PASS: idle STA messages, 4K image encoding and automatic memory reclamation.");

static void Assert(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS: " + label); }

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static WeakReference EnqueueInvalidImage(ClipboardActor actor, TaskCompletionSource<ClipboardWriteResult> completed)
{
    var text = "clipbridge:png:" + new string('!', 4 * 1024 * 1024);
    var reference = new WeakReference(text);
    Assert(actor.Enqueue(text, result => completed.TrySetResult(result)), "failed image accepted for retry testing");
    return reference;
}
