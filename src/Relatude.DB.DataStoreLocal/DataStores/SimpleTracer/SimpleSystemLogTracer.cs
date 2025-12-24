namespace Relatude.DB.DataStores.SimpleTracer;
/// <summary>
/// Simple in-memory tracer for system log entries. Thread-safe.
/// </summary>
internal class SimpleSystemLogTracer {
    readonly int maxEntries = 1000;
    readonly Queue<TraceEntry> _entries = [];
    public void Trace(SystemLogEntryType type, string text, string? details = null) {
        lock (_entries) {
            _entries.Enqueue(new TraceEntry(DateTime.UtcNow, type, text, details));
            while (_entries.Count > maxEntries) _entries.Dequeue();
        }
    }
    public DateTime GetLatest() {
        lock (_entries) {
            if (_entries.Count == 0) return DateTime.MinValue;
            return _entries.Last().Timestamp;
        }
    }
    public TraceEntry[] GetEntries(int skip, int take) {
        lock (_entries) {
            return [.. _entries.Reverse().Skip(skip).Take(take)];
        }
    }
}
