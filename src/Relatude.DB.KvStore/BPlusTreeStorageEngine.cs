using System.Buffers.Binary;
using System.Text;
using SuperFastIndex.Internal;

namespace SuperFastIndex;

/// <summary>Tuning options for <see cref="BPlusTreeStorageEngine"/>.</summary>
public sealed class BPlusTreeEngineOptions
{
    /// <summary>Memory budget for the page cache. Default 64 MiB.</summary>
    public long PageCacheBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Maximum number of decoded values cached per index to serve <see cref="ISortedIndex{T}.GetValue"/>
    /// without a tree descent. 0 (the default) disables the cache. Snapshot-consistent: every
    /// commit evicts the ids it touched. The budget is entries, not bytes — size it to the hot
    /// id working set and remember each entry holds one decoded value alive.
    /// </summary>
    public int ValueCacheEntries { get; init; }
}

/// <summary>Commit-time hook for indexes that keep a value cache (see <see cref="ValueCache{T}"/>).</summary>
internal interface IValueCacheOwner
{
    /// <summary>Called under the commit lock, after the new snapshot is published.</summary>
    void EvictCommittedIds(List<int>? touchedIds, bool overflow);
}

/// <summary>
/// Storage engine built on a copy-on-write B+Tree with shadow paging.
/// One writer at a time (a transaction), any number of concurrent readers: every
/// commit publishes an immutable snapshot, so reads never take a lock and never
/// block behind the writer. Commits with <c>durable: true</c> are power-loss
/// safe (data pages are flushed before the checksummed meta page that references
/// them); with <c>false</c> they are process-crash safe and much faster.
/// <para>
/// Pass a <c>null</c> path to the constructor for a memory-only engine: pages live
/// in memory instead of a file, nothing is persisted, and durability flags are no-ops.
/// </para>
/// </summary>
public sealed class BPlusTreeStorageEngine : IStorageEngine, IDisposable
{
    internal sealed record IndexState(byte TypeId, uint ValueRoot, uint IdRoot, int IdCount, int ValueCount);

    internal sealed class MutableIndexState
    {
        public byte TypeId;
        public uint ValueRoot;
        public uint IdRoot;
        public int IdCount;
        public int ValueCount;
        public bool Dirty;
        public List<int>? TouchedIds;   // ids this txn mutated, for value-cache eviction at commit
        public bool TouchedOverflow;    // txn touched more ids than the cache holds: clear instead

        public static MutableIndexState From(IndexState s) => new()
        {
            TypeId = s.TypeId, ValueRoot = s.ValueRoot, IdRoot = s.IdRoot, IdCount = s.IdCount, ValueCount = s.ValueCount,
        };

        public IndexState ToImmutable() => new(TypeId, ValueRoot, IdRoot, IdCount, ValueCount);
    }

    internal sealed class EngineSnapshot(long txId, long timestamp, Dictionary<string, IndexState> indexes)
    {
        public readonly long TxId = txId;
        public readonly long Timestamp = timestamp;
        public readonly Dictionary<string, IndexState> Indexes = indexes; // frozen after publication
    }

    internal sealed class WriteTxn(Pager pager, long txId, uint catalogRoot) : IWritePageSource
    {
        private readonly Pager _pager = pager;
        public readonly long TxId = txId;
        public readonly int OwnerThreadId = Environment.CurrentManagedThreadId;
        public readonly Dictionary<uint, byte[]> Dirty = new();
        public readonly List<uint> Freed = new();
        public uint CatalogRoot = catalogRoot;
        public readonly Dictionary<string, MutableIndexState> States = new();

        public byte[] GetPage(uint pageId)
            => Dirty.TryGetValue(pageId, out var p) ? p : _pager.GetPage(pageId);

        public (uint Id, byte[] Page) Allocate()
        {
            uint id = _pager.AllocatePage();
            var page = new byte[Pager.PageSize];
            Dirty[id] = page;
            return (id, page);
        }

        public (uint Id, byte[] Page) Cow(uint pageId)
        {
            if (Dirty.TryGetValue(pageId, out var owned))
                return (pageId, owned);
            var (id, page) = Allocate();
            _pager.GetPage(pageId).CopyTo(page, 0);
            Freed.Add(pageId);
            return (id, page);
        }

        public void Free(uint pageId)
        {
            if (Dirty.Remove(pageId))
                _pager.Recycle([pageId]); // never committed: reusable immediately
            else
                Freed.Add(pageId);
        }
    }

    private readonly Pager _pager;
    private readonly ReaderTable _readers = new();
    //private readonly Lock _writeLock = new();
    private readonly object _writeLock = new();
    private readonly Dictionary<string, object> _openIndexes = new();
    private readonly HashSet<string> _uncataloged = new(); // created but not yet persisted to the catalog
    private volatile EngineSnapshot _committed;
    private volatile WriteTxn? _activeTxn;

    internal int ValueCacheEntries { get; }
    internal long CommittedTxId => _committed.TxId;

    /// <summary>True when this engine keeps all data in memory and persists nothing (constructed with a null path).</summary>
    public bool IsMemoryOnly => _pager.IsMemoryOnly;

    /// <param name="path">Backing file for the database, or <c>null</c> for a memory-only engine.</param>
    /// <param name="options">Tuning options, or <c>null</c> for defaults.</param>
    public BPlusTreeStorageEngine(string? path, BPlusTreeEngineOptions? options = null)
    {
        options ??= new BPlusTreeEngineOptions();
        ValueCacheEntries = options.ValueCacheEntries;
        _pager = new Pager(path, options.PageCacheBytes);
        Meta meta = _pager.CurrentMeta;
        _committed = new EngineSnapshot(meta.TxId, meta.Timestamp, LoadCatalog(meta.CatalogRoot));
    }

    // ---- IStorageEngine ----

    public ISortedIndex<T> OpenOrCreateIndex<T>(string name) where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_writeLock)
        {
            if (_openIndexes.TryGetValue(name, out object? open))
            {
                return open as ISortedIndex<T>
                    ?? throw new InvalidOperationException($"Index '{name}' is already open with a different value type.");
            }

            bool existed = _committed.Indexes.TryGetValue(name, out var state);
            if (existed)
            {
                if (state!.TypeId != KeyCodec.GetTypeId<T>())
                    throw new InvalidOperationException($"Index '{name}' exists with a different value type.");
            }
            else
            {
                var fresh = new IndexState(KeyCodec.GetTypeId<T>(), 0, 0, 0, 0);
                var indexes = new Dictionary<string, IndexState>(_committed.Indexes) { [name] = fresh };
                _committed = new EngineSnapshot(_committed.TxId, _committed.Timestamp, indexes);
                _uncataloged.Add(name);
            }

            var index = new BPlusTreeIndex<T>(this, name, hasEngineTimestamp: existed);
            _openIndexes[name] = index;
            return index;
        }
    }

    public bool IsInTransaction => _activeTxn is not null;

    public void BeginTransaction()
    {
        lock (_writeLock)
        {
            if (_activeTxn is not null)
                throw new InvalidOperationException("A transaction is already active; this engine supports a single writer.");
            _pager.PromoteFreeBatches(_readers.MinActiveTxId());
            _activeTxn = new WriteTxn(_pager, _committed.TxId + 1, _pager.CurrentMeta.CatalogRoot);
        }
    }

    public void CommitTransaction(long timestamp, bool durable)
    {
        lock (_writeLock)
        {
            WriteTxn txn = _activeTxn ?? throw new InvalidOperationException("No active transaction.");
            var indexes = new Dictionary<string, IndexState>(_committed.Indexes);

            foreach (var (name, st) in txn.States)
            {
                if (st.Dirty)
                {
                    indexes[name] = st.ToImmutable();
                    _uncataloged.Add(name);
                }
            }
            foreach (string name in _uncataloged)
                WriteCatalogEntry(txn, name, indexes[name]);
            _uncataloged.Clear();

            _pager.Commit(txn.TxId, timestamp, txn.CatalogRoot, txn.Freed, txn.Dirty, durable);
            _committed = new EngineSnapshot(txn.TxId, timestamp, indexes);
            foreach (object open in _openIndexes.Values)
                ((IIndexTimestamp)open).AdoptEngineTimestamp();

            // Evict AFTER publishing: a populate racing this window re-checks CommittedTxId and
            // undoes itself, so no stale entry can outlive this method (see ValueCache docs).
            if (ValueCacheEntries > 0)
            {
                foreach (var (name, st) in txn.States)
                {
                    if (st.Dirty && _openIndexes.TryGetValue(name, out object? open) && open is IValueCacheOwner owner)
                        owner.EvictCommittedIds(st.TouchedIds, st.TouchedOverflow);
                }
            }
            _activeTxn = null;
        }
    }

    public void RollbackTransaction()
    {
        lock (_writeLock)
        {
            WriteTxn txn = _activeTxn ?? throw new InvalidOperationException("No active transaction.");
            _pager.Recycle(txn.Dirty.Keys);
            _activeTxn = null;
        }
    }

    public long GetTimestamp() => _committed.Timestamp;

    public void SetTimestamp(long timestamp)
    {
        lock (_writeLock)
        {
            if (_activeTxn is not null)
                throw new InvalidOperationException("SetTimestamp cannot run while a transaction is active; pass the timestamp to CommitTransaction instead.");
            long txId = _committed.TxId + 1;
            _pager.CommitMetaOnly(txId, timestamp, deepDiskFlush: true);
            _committed = new EngineSnapshot(txId, timestamp, _committed.Indexes);
            foreach (object open in _openIndexes.Values)
                ((IIndexTimestamp)open).AdoptEngineTimestamp();
        }
    }

    public long GetTotalDiskSpace() => _pager.IsMemoryOnly ? 0 : _pager.FileLength;

    public void DeleteAll()
    {
        lock (_writeLock)
        {
            if (_activeTxn is not null)
                throw new InvalidOperationException("DeleteAll cannot run while a transaction is active.");
            _pager.Reset();

            // Open indexes survive as empty, uncataloged definitions (persisted again on
            // their next commit); everything else — data and definitions — is gone.
            var indexes = new Dictionary<string, IndexState>();
            _uncataloged.Clear();
            foreach (var (name, open) in _openIndexes)
            {
                indexes[name] = new IndexState(_committed.Indexes[name].TypeId, 0, 0, 0, 0);
                _uncataloged.Add(name);
                if (open is IValueCacheOwner owner)
                    owner.EvictCommittedIds(null, overflow: true);
            }
            _committed = new EngineSnapshot(_pager.CurrentMeta.TxId, 0, indexes);
        }
    }

    public void DeleteUnopenedIndexes()
    {
        lock (_writeLock)
        {
            if (_activeTxn is not null)
                throw new InvalidOperationException("DeleteUnopenedIndexes cannot run while a transaction is active.");

            var doomed = new List<string>();
            foreach (string name in _committed.Indexes.Keys)
            {
                if (!_openIndexes.ContainsKey(name))
                    doomed.Add(name);
            }
            if (doomed.Count == 0)
                return;

            // A private mini-transaction: frees every page of the doomed trees and removes their
            // catalog entries, committed under the unchanged timestamp. Freed pages go through the
            // reader-protected free batches, so pinned snapshots can still walk them.
            _pager.PromoteFreeBatches(_readers.MinActiveTxId());
            var txn = new WriteTxn(_pager, _committed.TxId + 1, _pager.CurrentMeta.CatalogRoot);
            var indexes = new Dictionary<string, IndexState>(_committed.Indexes);
            foreach (string name in doomed)
            {
                IndexState st = indexes[name];
                FreeTree(txn, st.ValueRoot);
                FreeTree(txn, st.IdRoot);
                txn.CatalogRoot = BTree.Delete(txn, txn.CatalogRoot, Encoding.UTF8.GetBytes(name), out _);
                indexes.Remove(name);
            }
            _pager.Commit(txn.TxId, _committed.Timestamp, txn.CatalogRoot, txn.Freed, txn.Dirty, deepDiskFlush: true);
            _committed = new EngineSnapshot(txn.TxId, _committed.Timestamp, indexes);
        }
    }

    /// <summary>Frees every page of the tree rooted at <paramref name="root"/> (0 = empty tree).</summary>
    private static void FreeTree(WriteTxn txn, uint root)
    {
        if (root == 0)
            return;
        byte[] page = txn.GetPage(root);
        if (!NodePage.IsLeaf(page))
        {
            int count = NodePage.Count(page);
            for (int i = 0; i <= count; i++) // Count separator children + the rightmost
                FreeTree(txn, NodePage.ChildAt(page, i));
        }
        txn.Free(root);
    }

    public void Dispose() => _pager.Dispose();

    // ---- read/write access for indexes ----

    internal readonly struct ReadHandle : IDisposable
    {
        private readonly ReaderTable? _readers;
        private readonly int _slot;
        public readonly IPageSource Source;
        public readonly EngineSnapshot? Snapshot;
        public readonly WriteTxn? Txn;

        public ReadHandle(WriteTxn txn)
        {
            Txn = txn;
            Source = txn;
        }

        public ReadHandle(EngineSnapshot snapshot, IPageSource pages, ReaderTable readers, int slot)
        {
            Snapshot = snapshot;
            Source = pages;
            _readers = readers;
            _slot = slot;
        }

        public void Dispose() => _readers?.Release(_slot);
    }

    /// <summary>
    /// The writer thread sees its own in-flight transaction; any other thread gets a
    /// pinned, immutable committed snapshot readable without locks.
    /// </summary>
    internal ReadHandle BeginRead()
    {
        WriteTxn? txn = _activeTxn;
        if (txn is not null && txn.OwnerThreadId == Environment.CurrentManagedThreadId)
            return new ReadHandle(txn);

        // Pin BEFORE capturing the snapshot: the writer's reclaim scan then always sees
        // an id ≤ the snapshot we end up using, which conservatively protects its pages.
        int slot = _readers.Acquire(_committed.TxId);
        return new ReadHandle(_committed, _pager, _readers, slot);
    }

    internal WriteTxn RequireTxn()
    {
        WriteTxn? txn = _activeTxn;
        if (txn is null)
            throw new InvalidOperationException("Mutations require an active transaction (call BeginTransaction first).");
        if (txn.OwnerThreadId != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Write operations must run on the thread that started the transaction.");
        return txn;
    }

    internal MutableIndexState GetTxnState(WriteTxn txn, string name)
    {
        if (!txn.States.TryGetValue(name, out var st))
            txn.States[name] = st = MutableIndexState.From(_committed.Indexes[name]);
        return st;
    }

    internal IndexState GetCommittedState(EngineSnapshot snapshot, string name) => snapshot.Indexes[name];

    // ---- catalog: name -> [typeId:u8][valueRoot:u32][idRoot:u32][idCount:i32][valueCount:i32] ----

    private const int CatalogRecordSize = 17;

    private void WriteCatalogEntry(WriteTxn txn, string name, IndexState st)
    {
        Span<byte> record = stackalloc byte[CatalogRecordSize];
        record[0] = st.TypeId;
        BinaryPrimitives.WriteUInt32LittleEndian(record[1..], st.ValueRoot);
        BinaryPrimitives.WriteUInt32LittleEndian(record[5..], st.IdRoot);
        BinaryPrimitives.WriteInt32LittleEndian(record[9..], st.IdCount);
        BinaryPrimitives.WriteInt32LittleEndian(record[13..], st.ValueCount);
        txn.CatalogRoot = BTree.Insert(txn, txn.CatalogRoot, Encoding.UTF8.GetBytes(name), record, out _);
    }

    private Dictionary<string, IndexState> LoadCatalog(uint catalogRoot)
    {
        var indexes = new Dictionary<string, IndexState>();
        var cursor = new BTreeCursor(_pager);
        if (!cursor.SeekFirst(catalogRoot))
            return indexes;
        do
        {
            string name = Encoding.UTF8.GetString(cursor.Key);
            ReadOnlySpan<byte> r = cursor.Value;
            indexes[name] = new IndexState(
                r[0],
                BinaryPrimitives.ReadUInt32LittleEndian(r[1..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[5..]),
                BinaryPrimitives.ReadInt32LittleEndian(r[9..]),
                BinaryPrimitives.ReadInt32LittleEndian(r[13..]));
        } while (cursor.MoveNext());
        return indexes;
    }
}
