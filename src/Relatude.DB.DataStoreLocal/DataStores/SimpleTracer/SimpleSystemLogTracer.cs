namespace Relatude.DB.DataStores.SimpleTracer;
/// <summary>
/// Simple in-memory tracer for system log entries. Thread-safe.
/// </summary>
internal class SimpleSystemLogTracer {
    int maxEntries = 1000;
    Queue<TraceEntry> entries = [];
    public void Trace(SystemLogEntryType type, string text, string? details = null) {
        lock (entries) {
            entries.Enqueue(new TraceEntry(DateTime.UtcNow, type, text, details));
            while (entries.Count > maxEntries) entries.Dequeue();
        }
    }
    public TraceEntry[] GetEntries(int skip, int take) {
        lock (entries) {
            return entries.Reverse().Skip(skip).Take(take).ToArray();
        }
    }
}
