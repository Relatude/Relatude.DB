namespace Relatude.DB.VectorIndexHNSW;

/// <summary>
/// The "have I scored this node already" set of one graph walk, as a stamp per ordinal rather than a
/// hash set.
///
/// <para>A walk asks that question once for every candidate it considers — a few thousand times per
/// query — and answers it immediately before or instead of a dot product. At 1536 dimensions the dot
/// product is the expensive half; at 128 it is not, and hashing the candidate costs about as much as
/// scoring it. An array indexed by ordinal turns the question into one read and one write.</para>
///
/// <para>Starting a walk is a generation bump rather than a clear, so it stays O(1) whatever the index
/// size. The stamp is one byte, so the generation wraps every 255 walks and only then is the array
/// actually cleared — a megabyte of memset per million vectors, amortised to a few kilobytes a query.
/// One of these belongs to each pooled walk, which is what keeps concurrent searches from sharing it.</para>
/// </summary>
internal sealed class VisitedStamps {
    byte[] _stamps = [];
    byte _generation;

    /// <summary>Starts a walk over an index of <paramref name="capacity"/> ordinals. Everything stamped
    /// by an earlier walk reads as unvisited from here on.</summary>
    public void Begin(int capacity) {
        if (_stamps.Length < capacity) {
            _stamps = new byte[Math.Max(capacity + capacity / 4, 1024)]; // headroom, so growth is rare
            _generation = 0;
        }
        if (_generation == byte.MaxValue) {
            Array.Clear(_stamps);
            _generation = 0;
        }
        _generation++;
    }
    /// <summary>Marks an ordinal visited, returning false if this walk had already seen it. An ordinal
    /// outside the stamped range counts as seen, so it is never walked — <see cref="Begin"/> is always
    /// called with the index's ordinal count, so that can only be a stale neighbour id.</summary>
    public bool Add(int ordinal) {
        if ((uint)ordinal >= (uint)_stamps.Length) return false;
        if (_stamps[ordinal] == _generation) return false;
        _stamps[ordinal] = _generation;
        return true;
    }
}
