using System.Windows.Threading;

namespace ClipBridge.Windows;

internal static class ClipboardMessagePump
{
    internal static void Wait(AutoResetEvent signal, int timeout)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) =>
        {
            if (!dispatcher.HasShutdownStarted)
                _ = dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false));
        }, null, timeout, executeOnlyOnce: true);
        try { Dispatcher.PushFrame(frame); }
        finally { registration.Unregister(null); }
    }

    internal static void Drain()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
