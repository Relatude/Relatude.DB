namespace WAF.NodeServer;
public class ContainerLogEntry(string message) {
    public DateTime Timestamp { get; } = DateTime.UtcNow;
    public string Description { get; } = message;
}
public class ContainerLog(int capacity, TimeSpan maxAge) {
    Queue<ContainerLogEntry> _log { get; } = [];
    public void Add(string msg) {
        lock (_log) {
            _log.Enqueue(new ContainerLogEntry(msg));
            while (_log.Count > capacity) _log.Dequeue();
            // remove to old entries:
            var now = DateTime.UtcNow;
            while (_log.Count > 0 && now.Subtract(_log.Peek().Timestamp) > maxAge) _log.Dequeue();
        }
    }
    public IEnumerable<ContainerLogEntry> Get() {
        lock (_log) {
            return _log.ToArray();
        }
    }
    public void Clear() {
        lock (_log) {
            _log.Clear();
        }
    }
}
