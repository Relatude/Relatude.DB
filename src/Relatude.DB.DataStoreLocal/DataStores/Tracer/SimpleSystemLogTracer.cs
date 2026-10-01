namespace Relatude.DB.DataStores.Tracer;
/// <summary>
/// Simple in-memory tracer for system log entries. Thread-safe.
/// </summary>
internal class SimpleSystemLogTracer {
    readonly int maxEntries = 1000;
    readonly LinkedList<TraceEntry> _entries = [];
    /// <summary>Whether the last entry was written with <c>replace</c>: a progress line the next one may take the place of.</summary>
    bool _lastReplaceable;
    /// <summary>
    /// <paramref name="replace"/> marks a line that says how far something has come: it takes the
    /// place of the line before it, the way a terminal rewrites a progress line, so a long log replay
    /// is one line that moves rather than hundreds that push everything else out of the trace. Only a
    /// line written the same way is ever replaced - whatever else was traced in between stays, and
    /// the next progress line simply starts a new one below it.
    /// </summary>
    public void Trace(SystemLogEntryType type, string text, string? details = null, bool replace = false) {
        lock (_entries) {
            var entry = new TraceEntry(DateTime.UtcNow, type, text, details);
            if (replace && _lastReplaceable && _entries.Count > 0) _entries.RemoveLast();
            _entries.AddLast(entry);
            _lastReplaceable = replace;
            while (_entries.Count > maxEntries) _entries.RemoveFirst();
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
