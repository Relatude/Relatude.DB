using Relatude.DB.Datastores.Indexes.BTreeIndex;

namespace KvBenchmarks.Engines;

/// <summary>
/// The native engine driven the way production (NativeKvIndexStore) drives it: a non-durable
/// commit is <see cref="BPlusTreeStorageEngine.PublishTransaction"/> — readers see it immediately,
/// pages stay buffered in memory, and a crash rolls back to the last durable point — and a durable
/// commit is a full commit with a deep flush. That matches what the other engines' rows do with
/// durable: false (FASTER buffers in its log, ZoneTree in its async WAL) instead of paying a
/// meta write per batch that no other engine pays.
/// </summary>
public sealed class NativeBenchEngine(BPlusTreeStorageEngine inner) : IStorageEngine, IDisposable
{
    public ISortedIntIndex<T> OpenOrCreateIntIndex<T>(string name) where T : notnull => inner.OpenOrCreateIntIndex<T>(name);
    public ISortedUlongIndex<T> OpenOrCreateUlongIndex<T>(string name) where T : notnull => inner.OpenOrCreateUlongIndex<T>(name);
    public ISortedGuidIndex<T> OpenOrCreateGuidIndex<T>(string name) where T : notnull => inner.OpenOrCreateGuidIndex<T>(name);
    public IIntIndex<T> OpenOrCreateIntHashIndex<T>(string name) where T : notnull => inner.OpenOrCreateIntHashIndex<T>(name);
    public IUlongIndex<T> OpenOrCreateUlongHashIndex<T>(string name) where T : notnull => inner.OpenOrCreateUlongHashIndex<T>(name);
    public IGuidIndex<T> OpenOrCreateGuidHashIndex<T>(string name) where T : notnull => inner.OpenOrCreateGuidHashIndex<T>(name);

    public bool IsInTransaction => inner.IsInTransaction;
    public void BeginTransaction() => inner.BeginTransaction();

    public void CommitTransaction(long timestamp, bool durable)
    {
        if (durable)
            inner.CommitTransaction(timestamp, deepDiskFlush: true);
        else
            inner.PublishTransaction(timestamp);
    }

    public void RollbackTransaction() => inner.RollbackTransaction();
    public long GetTimestamp() => inner.GetTimestamp();
    public void SetTimestamp(long timestamp) => inner.SetTimestamp(timestamp);
    public long GetTotalDiskSpace() => inner.GetTotalDiskSpace();
    public void DeleteAll() => inner.DeleteAll();
    public void DeleteUnopenedIndexes() => inner.DeleteUnopenedIndexes();
    public void Dispose() => inner.Dispose();
}
