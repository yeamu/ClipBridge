using System.Diagnostics;
using System.Runtime;

namespace ClipBridge.Windows;

internal static class TransferMemory
{
    internal const long LargeTransferBytes = 4L * 1024 * 1024;
    private static readonly object Gate = new();
    private static int _active;
    private static bool _requested;
    private static long _lastActivity;
    private static long _finalizerPassAt;
    private static long _collectionCount;
    private static long _collectionTicks;
    private static readonly System.Threading.Timer Reclaimer = new(Reclaim, null, 1000, 1000);
    internal static long CollectionCount => Interlocked.Read(ref _collectionCount);
    internal static double LastCollectionMilliseconds => Interlocked.Read(ref _collectionTicks) * 1000d / Stopwatch.Frequency;

    internal static IDisposable BeginActivity()
    {
        lock (Gate) { _active++; _lastActivity = Stopwatch.GetTimestamp(); }
        return new Activity();
    }

    internal static void Released(long bytes)
    {
        if (bytes < LargeTransferBytes) return;
        lock (Gate) { _requested = true; _lastActivity = Stopwatch.GetTimestamp(); }
        GC.KeepAlive(Reclaimer);
    }

    private static void Reclaim(object? state)
    {
        bool finalizerPass;
        lock (Gate)
        {
            if (_active != 0) return;
            var idle = Stopwatch.GetElapsedTime(_lastActivity).TotalSeconds;
            if (_requested && idle >= 2)
            {
                _requested = false;
                _finalizerPassAt = 0;
                finalizerPass = false;
            }
            else if (_finalizerPassAt != 0 && Stopwatch.GetTimestamp() >= _finalizerPassAt && idle >= 1)
            {
                _finalizerPassAt = 0;
                finalizerPass = true;
            }
            else return;
        }
        // Coalesce image cleanup after the transfer's stack frames have returned.
        // No collection for ordinary text, and no synchronous wait on STA finalizers.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        var started = Stopwatch.GetTimestamp();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Interlocked.Exchange(ref _collectionTicks, Stopwatch.GetTimestamp() - started);
        Interlocked.Increment(ref _collectionCount);
        // WIC/COM wrappers can keep streams alive until their finalizers run.
        // Let the pumping STA release them, then reclaim their managed buffers.
        if (!finalizerPass) lock (Gate) _finalizerPassAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
    }

    private sealed class Activity : IDisposable
    {
        public void Dispose() { lock (Gate) { _active--; _lastActivity = Stopwatch.GetTimestamp(); } }
    }
}
