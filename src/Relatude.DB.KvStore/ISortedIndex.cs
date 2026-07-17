namespace SuperFastIndex;

/// <summary>
/// A bidirectional map between int ids and ordered values: point lookups in both directions
/// plus ordered range scans over the values. Entries are kept sorted by (value, id), so every
/// query returns a deterministic order. One value per id; many ids may share a value.
/// </summary>
public interface ISortedIndex<T> where T : notnull
{
    /// <summary>Number of entries (distinct ids).</summary>
    int Count { get; }

    /// <summary>Number of distinct values across all entries.</summary>
    int DistinctValueCount { get; }

    /// <summary>Maps <paramref name="id"/> to <paramref name="value"/>, replacing any existing mapping for that id.</summary>
    void Set(int id, T value);

    /// <summary>Removes the entry for <paramref name="id"/>; returns false if it was not present.</summary>
    bool Remove(int id);

    /// <summary>The value mapped to <paramref name="id"/>; throws <see cref="KeyNotFoundException"/> if absent.</summary>
    T GetValue(int id);

    /// <summary>Retrieves the value mapped to <paramref name="id"/>; returns false if absent.</summary>
    bool TryGetValue(int id, out T value);

    /// <summary>True if an entry exists for <paramref name="id"/>.</summary>
    bool ContainsKey(int id);

    /// <summary>True if at least one id is mapped to <paramref name="value"/>.</summary>
    bool ContainsValue(T value);

    /// <summary>All ids mapped to exactly <paramref name="value"/>, in ascending id order.</summary>
    IEnumerable<int> GetIds(T value);

    /// <summary>Every (id, value) entry, ordered by id.</summary>
    IEnumerable<KeyValuePair<int, T>> Entries { get; }

    /// <summary>Every id with an entry, in ascending id order.</summary>
    IEnumerable<int> Keys { get; }

    /// <summary>Every distinct value across all entries, in ascending value order.</summary>
    IEnumerable<T> DistinctValues { get; }

    /// <summary>The smallest value in the index; throws <see cref="InvalidOperationException"/> when the index is empty.</summary>
    T GetMinValue();

    /// <summary>The largest value in the index; throws <see cref="InvalidOperationException"/> when the index is empty.</summary>
    T GetMaxValue();

    /// <summary>
    /// Ids whose value lies between <paramref name="from"/> and <paramref name="to"/>, in
    /// ascending (value, id) order — or exactly reversed (descending value, then descending id)
    /// when <paramref name="descending"/> is true.
    /// </summary>
    IEnumerable<int> GetIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false);

    /// <summary>
    /// The same scan as <see cref="GetIdsInRange"/> with each id's value included. Values come
    /// out of the scan itself, never a per-id lookup, so this costs the same as ids alone.
    /// </summary>
    IEnumerable<KeyValuePair<int, T>> GetEntriesInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false);

    /// <summary>
    /// Ids whose value is greater than <paramref name="value"/> (or equal to it when
    /// <paramref name="includeValue"/>), in the same order contract as <see cref="GetIdsInRange"/>.
    /// </summary>
    IEnumerable<int> GetIdsGreaterThan(T value, bool includeValue = true, bool descending = false);

    /// <summary>
    /// Ids whose value is smaller than <paramref name="value"/> (or equal to it when
    /// <paramref name="includeValue"/>), in the same order contract as <see cref="GetIdsInRange"/>.
    /// </summary>
    IEnumerable<int> GetIdsSmallerThan(T value, bool includeValue = true, bool descending = false);

    /// <summary>Number of ids <see cref="GetIdsInRange"/> would yield for the same bounds.</summary>
    int CountIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true);

    /// <summary>Number of ids whose value is greater than <paramref name="value"/> (or equal to it when <paramref name="includeValue"/>).</summary>
    int CountIdsGreaterThan(T value, bool includeValue = true);

    /// <summary>Number of ids whose value is smaller than <paramref name="value"/> (or equal to it when <paramref name="includeValue"/>).</summary>
    int CountIdsSmallerThan(T value, bool includeValue = true);
}

/// <summary>
/// A named collection of <see cref="ISortedIndex{T}"/> instances sharing one writer transaction
/// and one engine-wide timestamp. A single writer at a time; reads are allowed from any thread
/// at any time (the isolation readers get varies by engine — see each engine's docs).
/// </summary>
public interface IStorageEngine
{
    /// <summary>
    /// Opens the index named <paramref name="name"/>, creating it if absent.
    /// Throws if the index already exists with a different value type.
    /// </summary>
    ISortedIndex<T> OpenOrCreateIndex<T>(string name) where T : notnull;

    /// <summary>Begins the single writer transaction; mutations require one.</summary>
    void BeginTransaction();

    /// <summary>
    /// True while the writer transaction is active — between <see cref="BeginTransaction"/> and
    /// the <see cref="CommitTransaction"/> or <see cref="RollbackTransaction"/> that ends it.
    /// </summary>
    bool IsInTransaction { get; }

    /// <summary>
    /// Publishes the transaction's changes and records <paramref name="timestamp"/>.
    /// With <paramref name="durable"/> the commit is flushed to stable storage (power-loss
    /// safe where the engine supports it); without, it trades durability for speed —
    /// see each engine's docs for the exact guarantee.
    /// </summary>
    void CommitTransaction(long timestamp, bool durable);

    /// <summary>Discards the transaction's changes.</summary>
    void RollbackTransaction();

    /// <summary>The timestamp recorded by the most recent commit or <see cref="SetTimestamp"/>.</summary>
    long GetTimestamp();

    /// <summary>Durably records <paramref name="timestamp"/>; only allowed outside a transaction.</summary>
    void SetTimestamp(long timestamp);

    /// <summary>Bytes of disk storage currently used by the engine (0 for memory-only engines).</summary>
    long GetTotalDiskSpace();

    /// <summary>
    /// Durably deletes every entry in every index and resets the timestamp to 0.
    /// Indexes that are open remain valid handles (now empty); indexes not currently open are
    /// erased entirely, definitions included. Only allowed outside a transaction, and not safe
    /// to call while other threads are reading.
    /// </summary>
    void DeleteAll();
}
