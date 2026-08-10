namespace Relatude.DB.Datastores.Indexes.BTreeIndex;

/// <summary>
/// A map from ids to values: one value per id, many ids may share a value.
/// Enumeration order is implementation-defined — see <see cref="ISortedDictionaryIndex{K,T}"/>
/// for the variant that also orders by value and <see cref="IIntIndex{T}"/> (and its ulong/Guid
/// siblings) for the unordered, lookup-only variant.
/// </summary>
public interface IDictionaryIndex<K, T> {

    /// <summary>Number of entries (distinct ids).</summary>
    int Count { get; }

    /// <summary>Maps <paramref name="id"/> to <paramref name="value"/>, replacing any existing mapping for that id.</summary>
    void Set(K id, T value);

    /// <summary>Removes the entry for <paramref name="id"/>; returns false if it was not present.</summary>
    bool Remove(K id);

    /// <summary>The value mapped to <paramref name="id"/>; throws <see cref="KeyNotFoundException"/> if absent.</summary>
    T GetValue(K id);

    /// <summary>Retrieves the value mapped to <paramref name="id"/>; returns false if absent.</summary>
    bool TryGetValue(K id, out T value);

    /// <summary>True if an entry exists for <paramref name="id"/>.</summary>
    bool ContainsKey(K id);

    /// <summary>All ids mapped to exactly <paramref name="value"/>, in this index's enumeration order.</summary>
    IEnumerable<K> GetIds(T value);

    /// <summary>Every (id, value) entry, in this index's enumeration order.</summary>
    IEnumerable<KeyValuePair<K, T>> Entries { get; }

    /// <summary>Every id with an entry, in this index's enumeration order.</summary>
    IEnumerable<K> Keys { get; }

    /// <summary>
    /// The engine timestamp this index is synchronized with: 0 for an index that was newly
    /// created (did not exist) and has not yet seen an engine commit or
    /// <see cref="IStorageEngine.SetTimestamp"/>; otherwise exactly the engine's timestamp.
    /// An index never carries a timestamp of its own.
    /// </summary>
    long GetTimestamp();

    /// <summary>
    /// Sets the index timestamp: 0 marks the index as not yet synchronized (as if newly created);
    /// the engine's current timestamp marks it synchronized. Any other value throws, since an
    /// index timestamp is always either 0 or the engine's (see <see cref="GetTimestamp"/>).
    /// </summary>
    void SetTimestamp(long timestamp);

}

/// <summary>
/// A bidirectional map between ids and ordered values: point lookups in both directions
/// plus ordered range scans over the values. Entries are kept sorted by (value, id), so every
/// query returns a deterministic order. One value per id; many ids may share a value.
/// <para>
/// The inherited members tighten their order contract here: <see cref="IDictionaryIndex{K,T}.Keys"/>
/// and <see cref="IDictionaryIndex{K,T}.Entries"/> yield ascending id order, and
/// <see cref="IDictionaryIndex{K,T}.GetIds"/> yields the ids of one value in ascending order.
/// </para>
/// </summary>
public interface ISortedDictionaryIndex<K, T>: IDictionaryIndex<K, T> where K : notnull where T : notnull {

    /// <summary>Number of distinct values across all entries.</summary>
    int DistinctValueCount { get; }

    /// <summary>True if at least one id is mapped to <paramref name="value"/>.</summary>
    bool ContainsValue(T value);

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
    IEnumerable<K> GetIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false);

    /// <summary>
    /// The same scan as <see cref="GetIdsInRange"/> with each id's value included. Values come
    /// out of the scan itself, never a per-id lookup, so this costs the same as ids alone.
    /// </summary>
    IEnumerable<KeyValuePair<K, T>> GetEntriesInRange(T from, T to, bool includeFrom = true, bool includeTo = true, bool descending = false);

    /// <summary>
    /// Ids whose value is greater than <paramref name="value"/> (or equal to it when
    /// <paramref name="includeValue"/>), in the same order contract as <see cref="GetIdsInRange"/>.
    /// </summary>
    IEnumerable<K> GetIdsGreaterThan(T value, bool includeValue = true, bool descending = false);

    /// <summary>
    /// Ids whose value is smaller than <paramref name="value"/> (or equal to it when
    /// <paramref name="includeValue"/>), in the same order contract as <see cref="GetIdsInRange"/>.
    /// </summary>
    IEnumerable<K> GetIdsSmallerThan(T value, bool includeValue = true, bool descending = false);

    /// <summary>Number of ids <see cref="GetIdsInRange"/> would yield for the same bounds.</summary>
    int CountIdsInRange(T from, T to, bool includeFrom = true, bool includeTo = true);

    /// <summary>Number of ids whose value is greater than <paramref name="value"/> (or equal to it when <paramref name="includeValue"/>).</summary>
    int CountIdsGreaterThan(T value, bool includeValue = true);

    /// <summary>Number of ids whose value is smaller than <paramref name="value"/> (or equal to it when <paramref name="includeValue"/>).</summary>
    int CountIdsSmallerThan(T value, bool includeValue = true);

}


/// <summary>
/// An int-keyed map with no ordering obligations: everything is reached through the id.
/// <see cref="IDictionaryIndex{K,T}.Keys"/>, <see cref="IDictionaryIndex{K,T}.Entries"/> and
/// <see cref="IDictionaryIndex{K,T}.GetIds"/> enumerate in an unspecified, unstable order, and
/// <see cref="IDictionaryIndex{K,T}.GetIds"/> is a full scan — dropping those guarantees is what
/// buys the constant-time lookups (see <c>OpenOrCreateIntHashIndex</c>). A sorted index satisfies
/// this contract too, so an engine without a dedicated hash layout can serve one.
/// <para>
/// Dropping the ordering also lifts any bound on value size: values are opaque payloads here, not
/// ordered keys, so an engine is free to store one across as many pages as it takes.
/// </para>
/// </summary>
public interface IIntIndex<T> : IDictionaryIndex<int, T> where T : notnull {
}

/// <summary>Same contract as <see cref="IIntIndex{T}"/>, keyed by ulong ids.</summary>
public interface IUlongIndex<T> : IDictionaryIndex<ulong, T> where T : notnull {
}

/// <summary>Same contract as <see cref="IIntIndex{T}"/>, keyed by Guid ids.</summary>
public interface IGuidIndex<T> : IDictionaryIndex<Guid, T> where T : notnull {
}

public interface ISortedIntIndex<T> : ISortedDictionaryIndex<int, T>, IIntIndex<T> where T : notnull {
}
public interface ISortedUlongIndex<T> : ISortedDictionaryIndex<ulong, T>, IUlongIndex<T> where T : notnull {
}
public interface ISortedGuidIndex<T> : ISortedDictionaryIndex<Guid, T>, IGuidIndex<T> where T : notnull {
}

/// <summary>
/// Engine-internal hook implemented by every index: flips the index to report the engine
/// timestamp. Called by the engine on every commit and <see cref="IStorageEngine.SetTimestamp"/>,
/// so an index timestamp is only ever 0 (new, unsynchronized) or the engine's current value.
/// </summary>
internal interface IIndexTimestamp {
    void AdoptEngineTimestamp();
}

/// <summary>
/// A named collection of <see cref="ISortedIntIndex{T}"/> instances sharing one writer transaction
/// and one engine-wide timestamp. A single writer at a time; reads are allowed from any thread
/// at any time (the isolation readers get varies by engine — see each engine's docs).
/// </summary>
public interface IStorageEngine {
    /// <summary>
    /// Opens the index named <paramref name="name"/>, creating it if absent.
    /// Throws if the index already exists with a different id or value type.
    /// An opened existing index reports the engine's timestamp; a newly created one reports 0
    /// until the next commit or <see cref="SetTimestamp"/> (see <see cref="ISortedIntIndex{T}.GetTimestamp"/>).
    /// </summary>
    ISortedIntIndex<T> OpenOrCreateSortedIntIndex<T>(string name) where T : notnull;

    /// <summary>Same contract as <see cref="OpenOrCreateSortedIntIndex{T}"/>, but the index is keyed by ulong ids. Engines that only support int ids throw <see cref="NotSupportedException"/>.</summary>
    ISortedUlongIndex<T> OpenOrCreateSortedUlongIndex<T>(string name) where T : notnull;

    /// <summary>Same contract as <see cref="OpenOrCreateSortedIntIndex{T}"/>, but the index is keyed by Guid ids. Engines that only support int ids throw <see cref="NotSupportedException"/>.</summary>
    ISortedGuidIndex<T> OpenOrCreateSortedGuidIndex<T>(string name) where T : notnull;

    /// <summary>
    /// Opens the unordered index named <paramref name="name"/>, creating it if absent — same
    /// storage, same transactions and same timestamp as <see cref="OpenOrCreateSortedIntIndex{T}"/>,
    /// but without any ordering (see <see cref="IIntIndex{T}"/>), which makes lookups by id and
    /// writes markedly cheaper and puts no bound on value size. A name belongs to one layout:
    /// opening a sorted index as a hash index (or the reverse) throws, as does a mismatched id or
    /// value type.
    /// <para>
    /// An engine with no cheaper unordered layout may return its sorted index, whose stronger
    /// guarantees satisfy this contract.
    /// </para>
    /// </summary>
    IIntIndex<T> OpenOrCreateIntHashIndex<T>(string name) where T : notnull;

    /// <summary>Same contract as <see cref="OpenOrCreateIntHashIndex{T}"/>, but the index is keyed by ulong ids.</summary>
    IUlongIndex<T> OpenOrCreateUlongHashIndex<T>(string name) where T : notnull;

    /// <summary>Same contract as <see cref="OpenOrCreateIntHashIndex{T}"/>, but the index is keyed by Guid ids.</summary>
    IGuidIndex<T> OpenOrCreateGuidHashIndex<T>(string name) where T : notnull;

    /// <summary>Begins the single writer transaction; mutations require one.</summary>
    void BeginTransaction();

    /// <summary>
    /// True while the writer transaction is active — between <see cref="BeginTransaction"/> and
    /// the <see cref="CommitTransaction"/> or <see cref="RollbackTransaction"/> that ends it.
    /// </summary>
    bool IsInTransaction { get; }

    /// <summary>
    /// Publishes the transaction's changes and records <paramref name="timestamp"/>,
    /// which every open index adopts (see <see cref="ISortedIntIndex{T}.GetTimestamp"/>).
    /// With <paramref name="durable"/> the commit is flushed to stable storage (power-loss
    /// safe where the engine supports it); without, it trades durability for speed —
    /// see each engine's docs for the exact guarantee.
    /// </summary>
    void CommitTransaction(long timestamp, bool durable);

    /// <summary>Discards the transaction's changes.</summary>
    void RollbackTransaction();

    /// <summary>The timestamp recorded by the most recent commit or <see cref="SetTimestamp"/>.</summary>
    long GetTimestamp();

    /// <summary>
    /// Durably records <paramref name="timestamp"/>, which every open index adopts
    /// (see <see cref="ISortedIntIndex{T}.GetTimestamp"/>); only allowed outside a transaction.
    /// </summary>
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

    /// <summary>
    /// Durably deletes every index present in the store that has not been opened in this session
    /// (via <see cref="OpenOrCreateSortedIntIndex{T}"/>), data and definition included; open indexes and the
    /// engine timestamp are untouched. Lets callers drop indexes that have left the schema, so a
    /// later re-add opens a fresh, empty index reporting timestamp 0 (see
    /// <see cref="ISortedIntIndex{T}.GetTimestamp"/>) instead of stale data claiming to be current.
    /// Only allowed outside a transaction.
    /// </summary>
    void DeleteUnopenedIndexes();
}
