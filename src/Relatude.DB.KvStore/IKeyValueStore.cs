namespace KvStore;

/// <summary>
/// The common surface of an embedded, ordered <c>byte[]</c>→<c>byte[]</c> key/value store: keys
/// are ordered by unsigned-byte (memcmp) comparison and <see cref="Range"/> returns slices in that
/// order. <see cref="Database"/> is the built-from-scratch B+tree implementation; alternative
/// backends (e.g. SQLite) implement the same contract so they can be compared apples-to-apples.
/// </summary>
public interface IKeyValueStore : IDisposable
{
    /// <summary>Number of entries currently stored.</summary>
    long Count { get; }

    /// <summary>Returns the value for <paramref name="key"/>, or null if absent.</summary>
    byte[]? Get(byte[] key);

    /// <summary>True if <paramref name="key"/> is present.</summary>
    bool ContainsKey(byte[] key);

    /// <summary>Inserts or overwrites a single entry and commits.</summary>
    void Put(byte[] key, byte[] value);

    /// <summary>Removes a single entry and commits. Returns true if the key existed.</summary>
    bool Delete(byte[] key);

    /// <summary>
    /// Returns the entries with key between <paramref name="start"/> and <paramref name="end"/> in
    /// ascending key order — descending when <paramref name="reverse"/> is set. Pass null for
    /// <paramref name="start"/> to scan from the beginning, or null for <paramref name="end"/> to
    /// scan to the end. Each present bound is included per its <c>*Inclusive</c> flag, so the
    /// default is the half-open interval <c>[start, end)</c>.
    /// </summary>
    IReadOnlyList<KeyValuePair<byte[], byte[]>> Range(
        byte[]? start = null, byte[]? end = null,
        bool startInclusive = true, bool endInclusive = false, bool reverse = false);

    /// <summary>
    /// Runs <paramref name="body"/> as a single atomic batch: all of its writes commit
    /// together, or none do if it throws. Set <paramref name="forceFlush"/> to make the
    /// batch durable immediately even when the backend defers flushing.
    /// </summary>
    void Batch(Action<IWriteBatch> body, bool forceFlush = false);

    /// <summary>
    /// Makes all committed-but-unflushed writes durable now. A no-op for backends that already
    /// fsync every commit.
    /// </summary>
    void Flush();
}

/// <summary>
/// The set of mutations applied within an <see cref="IKeyValueStore.Batch"/> call. Reads inside
/// the batch observe its own uncommitted writes.
/// </summary>
public interface IWriteBatch
{
    /// <summary>Inserts or overwrites an entry within the batch.</summary>
    void Put(byte[] key, byte[] value);

    /// <summary>Removes an entry within the batch. Returns true if the key existed.</summary>
    bool Delete(byte[] key);

    /// <summary>Reads a key, observing the batch's own uncommitted writes.</summary>
    byte[]? Get(byte[] key);
}
