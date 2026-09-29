namespace ClipBridge.Windows;

internal sealed class RecentMessageIds(int capacity = 4096)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _ids = [];
    private readonly Queue<string> _order = [];
    internal bool Contains(string id) { lock (_gate) return _ids.Contains(id); }
    internal bool Add(string id)
    {
        lock (_gate)
        {
            if (!_ids.Add(id)) return false;
            _order.Enqueue(id);
            while (_order.Count > capacity) _ids.Remove(_order.Dequeue());
            return true;
        }
    }
}
