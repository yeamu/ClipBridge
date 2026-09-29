namespace ClipBridge.Windows;

internal sealed class BoundedMemoryQueue<T>(Func<T, long> measure, int maxItems = 20, long budget = 64L * 1024 * 1024, long maxSingle = 320L * 1024 * 1024) where T : class
{
    private readonly object _gate = new();
    private readonly Queue<(T Item, long Bytes)> _items = new();
    private long _bytes;
    internal long Bytes { get { lock (_gate) return _bytes; } }
    internal bool CanAccept(long bytes)
    {
        lock (_gate) return bytes <= maxSingle && _items.Count < maxItems && (_items.Count == 0 || _bytes + bytes <= budget);
    }
    internal bool TryAdd(T item)
    {
        var bytes = measure(item);
        lock (_gate)
        {
            // One large clip may travel alone; it blocks additional accumulation.
            if (bytes > maxSingle || _items.Count >= maxItems || (_items.Count > 0 && _bytes + bytes > budget)) return false;
            _items.Enqueue((item, bytes));
            _bytes += bytes;
            return true;
        }
    }
    internal T? Peek() { lock (_gate) return _items.TryPeek(out var item) ? item.Item : null; }
    internal bool Complete(T item)
    {
        lock (_gate)
        {
            if (!_items.TryPeek(out var current) || !ReferenceEquals(current.Item, item)) return false;
            _items.Dequeue(); _bytes -= current.Bytes;
            return true;
        }
    }
    internal void Clear() { lock (_gate) { _items.Clear(); _bytes = 0; } }
}
