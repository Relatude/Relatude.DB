using System.Buffers.Binary;
using System.Text;
using Relatude.DB.Datastores.Indexes.BTreeIndex.Internal;

namespace Relatude.DB.Datastores.Indexes.BTreeIndex;

/// <summary>Tuning options for <see cref="BPlusTreeStorageEngine"/>.</summary>
public sealed class BPlusTreeEngineOptions
{
    /// <summary>Memory budget for the page cache. Default 64 MiB.</summary>
    public long PageCacheBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Maximum number of decoded values cached per index to serve <see cref="ISortedIntIndex{T}.GetValue"/>
    /// without a tree descent. 0 (the default) disables the cache. Snapshot-consistent: every
    /// commit evicts the ids it touched. The budget is entries, not bytes (rounded up to a power
    /// of two — the cache is a direct-mapped slot array) — size it to the hot id working set and
    /// remember each entry holds one decoded value alive.
    /// </summary>
    public int ValueCacheEntries { get; init; }
}

/// <summary>Commit-time hook for indexes that keep a value cache (see <see cref="ValueCache{TId,T}"/>).</summary>
internal interface IValueCacheOwner
{
    /// <summary>Called under the commit lock, after the new snapshot is published. <paramref name="touchedSlots"/> holds cache slot hashes (see <see cref="IdCodec{TId}.SlotHash"/>), not ids.</summary>
    void EvictCommittedSlots(List<int>? touchedSlots, bool overflow);
}

/// <summary>
/// Storage engine built on copy-on-write pages with shadow paging.
/// One writer at a time (a transaction), any number of concurrent readers: every
/// commit publishes an immutable snapshot, so reads never take a lock and never
/// block behind the writer. Commits with <c>durable: true</c> are power-loss
/// safe (data pages are flushed before the checksummed meta page that references
/// them); with <c>false</c> they are process-crash safe and much faster.
/// <para>
/// An index comes in one of two layouts, both living in the same file and the same transactions:
/// sorted (a pair of B+Trees — <see cref="BPlusTreeIndex{TId,T}"/>) for anything that needs order,
/// and hash (<see cref="HashIndex{TId,T}"/>) for pure lookup by id, which costs one page read per
/// lookup and one page copy per write instead of a descent and a whole copied path.
/// </para>
/// <para>
/// Pass a <c>null</c> path to the constructor for a memory-only engine: pages live
/// in memory instead of a file, nothing is persisted, and durability flags are no-ops.
/// </para>
/// </summary>
public sealed class BPlusTreeStorageEngine : IStorageEngine, IDisposable
{
    /// <summary>Sorted indexes are a pair of B+Trees; hash indexes are one extendible-hash table (see <see cref="HashIndex{TId,T}"/>) whose directory root lives in <c>IdRoot</c>.</summary>
    internal const byte LayoutSorted = 0;
    internal const byte LayoutHash = 1;

    /// <summary><paramref name="Dir"/> is the hash layout's in-memory bucket directory: null for a sorted index, and null for a hash index whose catalog entry has been read but which has not been opened yet.</summary>
    internal sealed record IndexState(byte TypeId, byte IdKind, byte Layout, uint ValueRoot, uint IdRoot, int IdCount, int ValueCount, HashDirectory? Dir);

    internal sealed class MutableIndexState
    {
        public byte TypeId;
        public byte IdKind;
        public byte Layout;
        public uint ValueRoot;
        public uint IdRoot;
        public int IdCount;
        public int ValueCount;
        public MutableHashDir? Dir;
        public bool Dirty;
        public List<int>? TouchedSlots; // value-cache slot hashes of the ids this txn mutated, for eviction at commit
        public bool TouchedOverflow;    // txn touched more ids than the cache holds: clear instead

        public static MutableIndexState From(IndexState s) => new()
        {
            TypeId = s.TypeId, IdKind = s.IdKind, Layout = s.Layout, ValueRoot = s.ValueRoot, IdRoot = s.IdRoot,
            IdCount = s.IdCount, ValueCount = s.ValueCount,
            Dir = s.Dir is null ? null : new MutableHashDir(s.Dir),
        };

        public IndexState ToImmutable() => new(TypeId, IdKind, Layout, ValueRoot, IdRoot, IdCount, ValueCount, Dir?.ToImmutable());
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
            // Uninitialized on purpose: a Cow overwrites all of it, and Init/insert paths write
            // the header and cells while the heap gap in between is never read back. Zeroing
            // fresh pages was a top CPU cost of write transactions.
            var page = GC.AllocateUninitializedArray<byte>(Pager.PageSize);
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

    public ISortedIntIndex<T> OpenOrCreateIntIndex<T>(string name) where T : notnull
        => OpenOrCreateCore<ISortedIntIndex<T>, T>(name, IdCodec<int>.Kind, LayoutSorted,
            existed => new BPlusTreeIntIndex<T>(this, name, hasEngineTimestamp: existed));

    /// <summary>Same contract as <see cref="OpenOrCreateIntIndex{T}"/>, but the index is keyed by ulong ids (<see cref="ISortedUlongIndex{T}"/>).</summary>
    public ISortedUlongIndex<T> OpenOrCreateUlongIndex<T>(string name) where T : notnull
        => OpenOrCreateCore<ISortedUlongIndex<T>, T>(name, IdCodec<ulong>.Kind, LayoutSorted,
            existed => new BPlusTreeUlongIndex<T>(this, name, hasEngineTimestamp: existed));

    /// <summary>Same contract as <see cref="OpenOrCreateIntIndex{T}"/>, but the index is keyed by Guid ids (<see cref="ISortedGuidIndex{T}"/>).</summary>
    public ISortedGuidIndex<T> OpenOrCreateGuidIndex<T>(string name) where T : notnull
        => OpenOrCreateCore<ISortedGuidIndex<T>, T>(name, IdCodec<Guid>.Kind, LayoutSorted,
            existed => new BPlusTreeGuidIndex<T>(this, name, hasEngineTimestamp: existed));

    /// <summary>
    /// Opens the unordered index named <paramref name="name"/>, creating it if absent: same file,
    /// same transactions and same timestamp as <see cref="OpenOrCreateIntIndex{T}"/>, but stored as
    /// one extendible-hash table instead of two B+Trees (see <see cref="HashIndex{TId,T}"/>) —
    /// lookups by id cost a single page read and writes copy a single page. A name belongs to one
    /// layout: opening a sorted index as a hash index or the reverse throws.
    /// </summary>
    public IIntIndex<T> OpenOrCreateIntHashIndex<T>(string name) where T : notnull
        => OpenOrCreateCore<IIntIndex<T>, T>(name, IdCodec<int>.Kind, LayoutHash,
            existed => new HashIntIndex<T>(this, name, hasEngineTimestamp: existed));

    /// <summary>Same contract as <see cref="OpenOrCreateIntHashIndex{T}"/>, but the index is keyed by ulong ids (<see cref="IUlongIndex{T}"/>).</summary>
    public IUlongIndex<T> OpenOrCreateUlongHashIndex<T>(string name) where T : notnull
        => OpenOrCreateCore<IUlongIndex<T>, T>(name, IdCodec<ulong>.Kind, LayoutHash,
            existed => new HashUlongIndex<T>(this, name, hasEngineTimestamp: existed));

    /// <summary>Same contract as <see cref="OpenOrCreateIntHashIndex{T}"/>, but the index is keyed by Guid ids (<see cref="IGuidIndex{T}"/>).</summary>
    public IGuidIndex<T> OpenOrCreateGuidHashIndex<T>(string name) where T : notnull
        => OpenOrCreateCore<IGuidIndex<T>, T>(name, IdCodec<Guid>.Kind, LayoutHash,
            existed => new HashGuidIndex<T>(this, name, hasEngineTimestamp: existed));

    private TIndex OpenOrCreateCore<TIndex, T>(string name, byte idKind, byte layout, Func<bool, TIndex> create)
        where TIndex : class
        where T : notnull
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_writeLock)
        {
            bool existed = _committed.Indexes.TryGetValue(name, out var state);
            if (existed)
            {
                if (state!.TypeId != KeyCodec.GetTypeId<T>() || state.IdKind != idKind)
                    throw new InvalidOperationException($"Index '{name}' exists with a different id or value type.");
                // Checked before the open-index shortcut below: a sorted index also satisfies the
                // unordered interfaces, so the cast alone would happily hand one out.
                if (state.Layout != layout)
                    throw new InvalidOperationException($"Index '{name}' exists as {(state.Layout == LayoutHash ? "a hash" : "a sorted")} index; a name cannot hold both layouts.");
            }

            if (_openIndexes.TryGetValue(name, out object? open))
            {
                return open as TIndex
                    ?? throw new InvalidOperationException($"Index '{name}' is already open with a different id or value type.");
            }

            if (existed)
            {
                // The catalog only names the directory root; the directory itself is read once,
                // here, and from then on travels with the snapshot that published it.
                if (layout == LayoutHash && state!.Dir is null)
                    ReplaceCommittedState(name, state with { Dir = HashDirectoryStore.Load(_pager, state.IdRoot) });
            }
            else
            {
                ReplaceCommittedState(name, new IndexState(KeyCodec.GetTypeId<T>(), idKind, layout, 0, 0, 0, 0,
                    layout == LayoutHash ? HashDirectory.CreateEmpty() : null));
                _uncataloged.Add(name);
            }

            TIndex index = create(existed);
            _openIndexes[name] = index;
            return index;
        }
    }

    /// <summary>Publishes a new snapshot differing only in one index's state. Callers hold the write lock.</summary>
    private void ReplaceCommittedState(string name, IndexState state)
    {
        var indexes = new Dictionary<string, IndexState>(_committed.Indexes) { [name] = state };
        _committed = new EngineSnapshot(_committed.TxId, _committed.Timestamp, indexes);
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

    public void CommitTransaction(long timestamp, bool deepDiskFlush)
    {
        lock (_writeLock)
        {
            WriteTxn txn = _activeTxn ?? throw new InvalidOperationException("No active transaction.");
            var indexes = new Dictionary<string, IndexState>(_committed.Indexes);

            foreach (var (name, st) in txn.States)
            {
                if (st.Dirty)
                {
                    // A hash index keeps its directory in memory during the transaction; the pages
                    // it dirtied are written here, into this same commit.
                    if (st.Layout == LayoutHash)
                        st.IdRoot = HashDirectoryStore.Persist(txn, st.Dir!);
                    indexes[name] = st.ToImmutable();
                    _uncataloged.Add(name);
                }
            }
            foreach (string name in _uncataloged)
                WriteCatalogEntry(txn, name, indexes[name]);
            _uncataloged.Clear();

            _pager.Commit(txn.TxId, timestamp, txn.CatalogRoot, txn.Freed, txn.Dirty, deepDiskFlush);
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
                        owner.EvictCommittedSlots(st.TouchedSlots, st.TouchedOverflow);
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
                IndexState old = _committed.Indexes[name];
                indexes[name] = new IndexState(old.TypeId, old.IdKind, old.Layout, 0, 0, 0, 0,
                    old.Layout == LayoutHash ? HashDirectory.CreateEmpty() : null);
                _uncataloged.Add(name);
                if (open is IValueCacheOwner owner)
                    owner.EvictCommittedSlots(null, overflow: true);
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
                if (st.Layout == LayoutHash)
                {
                    HashDirectoryStore.FreeAll(txn, st.IdRoot);
                }
                else
                {
                    FreeTree(txn, st.ValueRoot);
                    FreeTree(txn, st.IdRoot);
                }
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

    // ---- catalog: name -> [typeId:u8][valueRoot:u32][idRoot:u32][idCount:i32][valueCount:i32][idKind:u8][layout:u8] ----
    // idKind and layout were appended later, one at a time: a shorter record from a file written
    // before a field existed reads as 0, which is what every index was back then (int ids, sorted).
    // A hash index has no value tree; its directory root is stored in idRoot.

    private const int CatalogRecordSize = 19;
    private const int CatalogIdKindOffset = 17;
    private const int CatalogLayoutOffset = 18;

    private void WriteCatalogEntry(WriteTxn txn, string name, IndexState st)
    {
        Span<byte> record = stackalloc byte[CatalogRecordSize];
        record[0] = st.TypeId;
        BinaryPrimitives.WriteUInt32LittleEndian(record[1..], st.ValueRoot);
        BinaryPrimitives.WriteUInt32LittleEndian(record[5..], st.IdRoot);
        BinaryPrimitives.WriteInt32LittleEndian(record[9..], st.IdCount);
        BinaryPrimitives.WriteInt32LittleEndian(record[13..], st.ValueCount);
        record[CatalogIdKindOffset] = st.IdKind;
        record[CatalogLayoutOffset] = st.Layout;
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
                r.Length > CatalogIdKindOffset ? r[CatalogIdKindOffset] : (byte)0,
                r.Length > CatalogLayoutOffset ? r[CatalogLayoutOffset] : (byte)0,
                BinaryPrimitives.ReadUInt32LittleEndian(r[1..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[5..]),
                BinaryPrimitives.ReadInt32LittleEndian(r[9..]),
                BinaryPrimitives.ReadInt32LittleEndian(r[13..]),
                Dir: null); // loaded on first open, not for every index in the file
        } while (cursor.MoveNext());
        return indexes;
    }
}
