using System.Buffers.Binary;
using System.Text;
using KvStore.BTree;
using KvStore.Paging;
using KvStore.Serialization;

namespace KvStore;

/// <summary>
/// The byte-level engine shared by every public face. One engine owns one file, which holds any
/// number of named <b>tables</b> (independent B+trees) plus a <b>catalog</b> tree mapping each
/// table name to its record <c>{root page, entry count}</c>. Table roots are lazy: an empty table
/// is only an in-memory handle, and its root leaf (plus catalog record) materialise inside the
/// transaction that first writes to it — so creation rolls back like any other write.
///
/// <para>This is the <b>only</b> place locking and flush orchestration live: a single-writer /
/// multi-reader <see cref="ReaderWriterLockSlim"/> serialises writers against readers, and (in
/// async mode) a background thread flushes staged batches on an interval.</para>
///
/// <para><b>Write units.</b> A <see cref="Batch"/> is the atomic auto-commit unit (begin → body →
/// stage). An explicit <b>transaction</b> (<see cref="StartTransaction"/> /
/// <see cref="CommitTransaction"/> / <see cref="CancelTransaction"/>) holds the write lock across
/// arbitrarily many operations on any tables until it commits or cancels — one at a time, by
/// design. Reads issued from the transacting thread bypass the read lock and therefore see the
/// transaction's own uncommitted writes (through the pager's dirty layer); all other threads
/// block until the transaction ends. <see cref="CommitTransaction"/> stamps the caller's
/// timestamp into the meta page, so <see cref="CommitTimestamp"/> is readable after reopen and
/// crash-consistent with the data it stamped.</para>
///
/// <para>Keys and values are raw <c>byte[]</c> here; typed encoding is the caller's job. Each
/// table's ordering is supplied as an <see cref="IByteKeyComparer"/> when it is opened.</para>
/// </summary>
internal sealed class StorageEngine : IDisposable
{
    /// <summary>Maximum combined on-disk size of one key/value entry, including framing overhead.</summary>
    public const int MaxEntrySize = Pager.PageSize / 4;

    private readonly Pager _pager;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly FlushMode _flushMode;
    private readonly int _flushDelayMs;

    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread? _flusher;
    private bool _disposed;

    // Group commit: commits are numbered under the write lock; a flush leader (holding
    // _flushLock) snapshots everything staged so far and makes it durable with one fsync,
    // then publishes the highest sequence covered. Committers that arrive while an fsync is
    // in flight queue on _flushLock and are usually satisfied by the next leader's single
    // fsync rather than paying one each. Lock order is always _flushLock -> write lock.
    private readonly object _flushLock = new();
    private long _commitSeq;
    private long _durableSeq;

    // ---- Tables & catalog ---------------------------------------------------

    /// <summary>Catalog record: [u32 root page][u64 entry count], keyed by the UTF-8 table name.
    /// Fixed-length, so steady-state catalog updates take the in-place overwrite fast path.
    /// The byte layout lives only in the encode/decode pair below.</summary>
    private const int CatalogRecordSize = 12;

    private static void EncodeCatalogRecord(Span<byte> dst, uint root, ulong count)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(dst, root);
        BinaryPrimitives.WriteUInt64LittleEndian(dst[4..], count);
    }

    private static (uint root, ulong count) DecodeCatalogRecord(ReadOnlySpan<byte> src)
        => (BinaryPrimitives.ReadUInt32LittleEndian(src), BinaryPrimitives.ReadUInt64LittleEndian(src[4..]));

    // The catalog tree (memcmp order over UTF-8 names) whose root lives in the meta page.
    private readonly BPlusTree<RawByteComparer> _catalog;

    // Every table opened through this engine, by name. Mutated under the write lock.
    private readonly Dictionary<string, Table> _tables = new();

    /// <summary>
    /// A named B+tree plus its catalog-backed state. <see cref="Root"/>/<see cref="EntryCount"/>
    /// are the live (possibly uncommitted) values; <see cref="SavedRoot"/>/<see cref="SavedCount"/>
    /// mirror the catalog record as of the last successful commit and are what a rollback restores
    /// — they only advance in <see cref="CommitOpenChanges"/>, after staging succeeded.
    /// </summary>
    internal sealed class Table
    {
        public required byte[] NameBytes { get; init; }
        public required IBPlusTree Tree { get; init; }
        public required TreeRoot Root { get; init; }
        public ulong EntryCount;
        public uint SavedRoot;
        public ulong SavedCount;

        /// <summary>Committed state (<see cref="SavedRoot"/>/<see cref="SavedCount"/>) not yet
        /// written to the catalog tree. Catalog write-back is deferred to flush time — the flush
        /// leader folds the records into the same WAL record as the data they describe — so a
        /// tiny commit doesn't pay a catalog-page copy of its own.</summary>
        public bool CatalogDirty;
    }

    // The explicit transaction's owning thread (-1 when none). Operations from this thread bypass
    // the locks (the thread already holds the write lock); everyone else queues on the locks.
    private int _txnThreadId = -1;

    private bool OnTransactionThread => Volatile.Read(ref _txnThreadId) == Environment.CurrentManagedThreadId;

    private StorageEngine(Pager pager, DatabaseOptions options)
    {
        _pager = pager;
        _catalog = new BPlusTree<RawByteComparer>(pager, default, pager.CatalogRoot);
        _flushMode = options.FlushMode;
        _flushDelayMs = Math.Max(1, options.FlushDelayMs);

        if (_flushMode == FlushMode.Async)
        {
            _flusher = new Thread(FlushLoop) { IsBackground = true, Name = "kvstore-flush" };
            _flusher.Start();
        }
    }

    public static StorageEngine Open(string path, DatabaseOptions options)
    {
        options.Validate();
        return new StorageEngine(Pager.Open(path, options.CacheSizeBytes), options);
    }

    /// <summary>Bytes the database currently occupies on disk (main file + WAL sidecar).</summary>
    public long TotalDiskBytes => _pager.TotalDiskBytes;

    // Test hooks (exposed to KvStore.Tests via InternalsVisibleTo).
    internal int CleanCacheCount => _pager.CleanCacheCount;
    internal int StagedPageCount => _pager.StagedPageCount;
    internal int WalPageCount => _pager.WalPageCount;

    // ---- Table access -------------------------------------------------------

    /// <summary>Opens (or creates a handle for) the named table, ordered by <paramref name="cmp"/>.
    /// Idempotent per name; the caller must always use the same ordering for a given table.</summary>
    public Table GetTable<TCmp>(string name, TCmp cmp)
        where TCmp : struct, IByteKeyComparer
    {
        ArgumentNullException.ThrowIfNull(name);
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool locked = !OnTransactionThread;
        if (locked) _lock.EnterWriteLock();
        try
        {
            if (_tables.TryGetValue(name, out var existing)) return existing;

            var nameBytes = Encoding.UTF8.GetBytes(name);
            var root = new TreeRoot();
            ulong count = 0;
            if (_catalog.TryGet(nameBytes, static v => DecodeCatalogRecord(v), out var rec))
            {
                root.PageId = rec.root;
                count = rec.count;
            }

            var table = new Table
            {
                NameBytes = nameBytes,
                Tree = new BPlusTree<TCmp>(_pager, cmp, root),
                Root = root,
                EntryCount = count,
                SavedRoot = root.PageId,
                SavedCount = count,
            };
            _tables[name] = table;
            return table;
        }
        finally
        {
            if (locked) _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Opens the named table ordered by <paramref name="keyCodec"/>. Built-in codecs have a fixed
    /// encoded ordering, so each maps to a specialised struct comparer that inlines every
    /// comparison — <see cref="CodecKeyComparer{TKey}"/> would pay an interface dispatch per
    /// binary-search step instead. Each arm must order exactly like that codec's CompareEncoded.
    /// </summary>
    public Table GetTable<TKey>(string name, IKeyCodec<TKey> keyCodec)
        => keyCodec switch
        {
            Codecs.BoolCodec or Codecs.ByteCodec => GetTable(name, new UInt8KeyComparer()),
            Codecs.SByteCodec => GetTable(name, new Int8KeyComparer()),
            Codecs.Int16Codec => GetTable(name, new Int16LeKeyComparer()),
            Codecs.UInt16Codec => GetTable(name, new UInt16LeKeyComparer()),
            Codecs.Int32Codec => GetTable(name, new Int32LeKeyComparer()),
            Codecs.UInt32Codec => GetTable(name, new UInt32LeKeyComparer()),
            Codecs.Int64Codec or Codecs.DateTimeCodec => GetTable(name, new Int64LeKeyComparer()),
            Codecs.UInt64Codec => GetTable(name, new UInt64LeKeyComparer()),
            Codecs.SingleCodec => GetTable(name, new SingleLeKeyComparer()),
            Codecs.DoubleCodec => GetTable(name, new DoubleLeKeyComparer()),
            Codecs.GuidCodec => GetTable(name, new GuidKeyComparer()),
            Codecs.ByteArrayCodec => GetTable(name, new RawByteComparer()),
            _ => GetTable(name, new CodecKeyComparer<TKey>(keyCodec)),
        };

    /// <summary>Names of all tables that exist on disk (in the catalog) or hold data in memory.
    /// Handles that were opened but never written to are not listed.</summary>
    public IReadOnlyList<string> TableNames
    {
        get
        {
            bool locked = EnterReadIfNeeded();
            try
            {
                var names = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var name in _catalog.Range<string>(
                    null, null, static (k, _) => Encoding.UTF8.GetString(k), true, false, false, 0))
                    names.Add(name);
                foreach (var (name, table) in _tables)
                    if (table.Root.PageId != Pager.NullPage) names.Add(name);
                return names.ToList();
            }
            finally { ExitReadIfNeeded(locked); }
        }
    }

    // ---- Reads ------------------------------------------------------------

    // Reads from the transacting thread skip the read lock: that thread already holds the write
    // lock (so a read-lock attempt would throw), and skipping it is exactly what makes the open
    // transaction's writes visible to its own Get/Range.
    private bool EnterReadIfNeeded()
    {
        if (OnTransactionThread) return false;
        _lock.EnterReadLock();
        return true;
    }

    private void ExitReadIfNeeded(bool entered)
    {
        if (entered) _lock.ExitReadLock();
    }

    public long Count(Table table)
    {
        bool locked = EnterReadIfNeeded();
        try { return (long)table.EntryCount; }
        finally { ExitReadIfNeeded(locked); }
    }

    public byte[]? Get(Table table, byte[] key)
        => TryGet(table, key, static v => v.ToArray(), out var value) ? value : null;

    /// <summary>Looks up a key and decodes the value straight from the page span via
    /// <paramref name="selector"/> (run under the read lock), skipping the byte[] copy.</summary>
    public bool TryGet<T>(Table table, ReadOnlySpan<byte> key, ValueSelector<T> selector, out T value)
    {
        bool locked = EnterReadIfNeeded();
        try { return table.Tree.TryGet(key, selector, out value); }
        finally { ExitReadIfNeeded(locked); }
    }

    /// <summary>Projects the smallest-key entry via <paramref name="selector"/> (one descent,
    /// no scan); false when the table is empty.</summary>
    public bool TryFirst<T>(Table table, EntrySelector<T> selector, out T result)
    {
        bool locked = EnterReadIfNeeded();
        try { return table.Tree.TryFirst(selector, out result); }
        finally { ExitReadIfNeeded(locked); }
    }

    /// <summary>Projects the largest-key entry via <paramref name="selector"/> (one descent,
    /// no scan); false when the table is empty.</summary>
    public bool TryLast<T>(Table table, EntrySelector<T> selector, out T result)
    {
        bool locked = EnterReadIfNeeded();
        try { return table.Tree.TryLast(selector, out result); }
        finally { ExitReadIfNeeded(locked); }
    }

    /// <summary>
    /// Materialised scan of the entries between <paramref name="start"/> and <paramref name="end"/>,
    /// each projected by <paramref name="selector"/> directly from the page spans (under the read
    /// lock — no intermediate byte[] copies). Each present bound is included per its
    /// <c>*Inclusive</c> flag (default <c>[start, end)</c>); pass <paramref name="reverse"/> for
    /// descending key order.
    /// </summary>
    public List<T> Range<T>(
        Table table, byte[]? start, byte[]? end, EntrySelector<T> selector,
        bool startInclusive = true, bool endInclusive = false, bool reverse = false)
    {
        bool locked = EnterReadIfNeeded();
        try
        {
            // A full scan returns every entry; pre-size the list so it never reallocates.
            int capacityHint = start is null && end is null
                ? (int)Math.Min(table.EntryCount, int.MaxValue)
                : 0;
            return table.Tree.Range(start, end, selector, startInclusive, endInclusive, reverse, capacityHint);
        }
        finally { ExitReadIfNeeded(locked); }
    }

    // ---- Batches (the atomic auto-commit write unit) ------------------------

    /// <summary>
    /// Runs <paramref name="body"/> as one atomic batch against <paramref name="table"/> under the
    /// write lock. On success the writes are staged; they are flushed immediately when in sync mode
    /// or when <paramref name="forceFlush"/> is set, otherwise the background flusher persists them
    /// later. A throwing body rolls back only this batch. Inside an open transaction (same thread)
    /// the body simply joins the transaction: its writes commit or cancel with it, and
    /// <paramref name="forceFlush"/> is deferred to the transaction's own durability.
    /// </summary>
    public void Batch(Table table, Action<ByteWriteBatch> body, bool forceFlush)
    {
        ArgumentNullException.ThrowIfNull(body);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (OnTransactionThread)
        {
            body(new ByteWriteBatch(this, table));
            return;
        }

        bool mustFlush = _flushMode == FlushMode.Sync || forceFlush;
        long seq;

        _lock.EnterWriteLock();
        try
        {
            _pager.BeginTransaction();
            try
            {
                body(new ByteWriteBatch(this, table));
                seq = FinishCommit(mustFlush);
            }
            catch
            {
                RollbackOpenChanges();
                throw;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        // The fsync happens outside the write lock so other writers can build (and stage) their
        // batches while this one becomes durable — they then share the next leader's fsync.
        if (mustFlush) WaitDurable(seq);
    }

    /// <summary>
    /// Commits one insert as its own atomic batch — the common <c>Put(key, value)</c> shape —
    /// without <see cref="Batch"/>'s delegate and wrapper allocations (spans cannot be captured
    /// by a closure, so this is a dedicated path rather than a Batch call). Same commit protocol
    /// and transaction-join behaviour as <see cref="Batch"/>. Returns true if the key was new.
    /// </summary>
    public bool PutOne(Table table, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool forceFlush)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (OnTransactionThread) return InsertBytes(table, key, value);

        bool mustFlush = _flushMode == FlushMode.Sync || forceFlush;
        long seq;
        bool isNew;

        _lock.EnterWriteLock();
        try
        {
            _pager.BeginTransaction();
            try
            {
                isNew = InsertBytes(table, key, value);
                seq = FinishCommit(mustFlush);
            }
            catch
            {
                RollbackOpenChanges();
                throw;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        if (mustFlush) WaitDurable(seq);
        return isNew;
    }

    /// <summary>
    /// Deletes every entry in every table — the whole file's contents — as one atomic commit.
    /// O(1) in the data size: the catalog, free list, and page count reset to the fresh-file
    /// state, so every tree becomes rootless (the lazy-root form of "empty") and the orphaned
    /// pages' space is reused by later writes (the physical file does not shrink). The commit
    /// timestamp is preserved. Joins an open transaction like any other write (a cancel restores
    /// everything); durability otherwise follows the flush mode, exactly like a batch.
    /// </summary>
    public void DeleteAll(bool forceFlush)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (OnTransactionThread)
        {
            WipeOpenState();
            return;
        }

        bool mustFlush = _flushMode == FlushMode.Sync || forceFlush;
        long seq;

        _lock.EnterWriteLock();
        try
        {
            _pager.BeginTransaction();
            try
            {
                WipeOpenState();
                seq = FinishCommit(mustFlush);
            }
            catch
            {
                RollbackOpenChanges();
                throw;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        if (mustFlush) WaitDurable(seq);
    }

    // Resets the pager meta and every open table to the fresh-file state, inside the current
    // pager transaction. Dirtying the meta page is what carries a pure wipe (no node page
    // changes) into the flush snapshot and the WAL — PrepareFlush refreshes its content from
    // the meta fields at flush time, and a rollback discards the dirty copy with the rest.
    private void WipeOpenState()
    {
        _pager.GetWritable(Pager.MetaPageId);

        _pager.CatalogRoot.PageId = Pager.NullPage;
        _pager.FreeListHead = Pager.NullPage;
        _pager.PageCount = 1;
        _catalog.InvalidateTailHint();

        foreach (var table in _tables.Values)
        {
            table.Root.PageId = Pager.NullPage;
            table.EntryCount = 0;
            table.Tree.InvalidateTailHint();
        }
    }

    /// <summary>Commits one delete as its own atomic batch; see <see cref="PutOne"/>.
    /// Returns true if the key existed.</summary>
    public bool DeleteOne(Table table, ReadOnlySpan<byte> key, bool forceFlush)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (OnTransactionThread) return DeleteBytes(table, key);

        bool mustFlush = _flushMode == FlushMode.Sync || forceFlush;
        long seq;
        bool removed;

        _lock.EnterWriteLock();
        try
        {
            _pager.BeginTransaction();
            try
            {
                removed = DeleteBytes(table, key);
                seq = FinishCommit(mustFlush);
            }
            catch
            {
                RollbackOpenChanges();
                throw;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        if (mustFlush) WaitDurable(seq);
        return removed;
    }

    /// <summary>Commit tail shared by every committer (batch, single-op, transaction): completes
    /// the open pager transaction and claims the commit's durability sequence number when it must
    /// flush. Must run under the write lock.</summary>
    private long FinishCommit(bool mustFlush)
    {
        CommitOpenChanges();
        return mustFlush ? ++_commitSeq : 0;
    }

    /// <summary>
    /// Completes the open pager transaction: stages the whole set, then advances the tables'
    /// saved state (marking changed tables for flush-time catalog write-back). Must run under the
    /// write lock. Saved state only moves after staging succeeded, so a throw anywhere leaves
    /// rollback able to restore the exact pre-transaction table states.
    /// </summary>
    private void CommitOpenChanges()
    {
        _pager.StageTransaction();

        foreach (var table in _tables.Values)
        {
            if (table.Root.PageId == table.SavedRoot && table.EntryCount == table.SavedCount) continue;
            table.SavedRoot = table.Root.PageId;
            table.SavedCount = table.EntryCount;
            table.CatalogDirty = true;
        }
    }

    /// <summary>
    /// Writes every catalog-dirty table's committed record into the catalog tree. Runs on the
    /// flush leader, under the write lock, with no batch open — the pages it dirties are folded
    /// into the flush snapshot by <c>PrepareFlush</c>, so each WAL record carries the catalog
    /// state matching the data it covers (a crash recovers table roots/counts and their pages as
    /// one atomic unit). Deferring this to flush time is what keeps tiny commits cheap: in async
    /// mode thousands of small batches share one catalog-page update per flush, while sync mode
    /// (which flushes every commit) behaves exactly as if it were written per commit.
    /// </summary>
    private void WriteBackCatalogRecords()
    {
        Span<byte> rec = stackalloc byte[CatalogRecordSize];
        foreach (var table in _tables.Values)
        {
            if (!table.CatalogDirty) continue;
            if (table.SavedRoot == Pager.NullPage)
            {
                // The table has no pages (wiped by DeleteAll): it must not exist in the catalog —
                // a table exists there iff it has a root, matching lazy creation.
                _catalog.Delete(table.NameBytes);
            }
            else
            {
                EncodeCatalogRecord(rec, table.SavedRoot, table.SavedCount);
                _catalog.Insert(table.NameBytes, rec);
            }
            table.CatalogDirty = false;
        }
    }

    /// <summary>Discards the open pager transaction and restores every table (and the catalog)
    /// to its last committed state. Must run under the write lock.</summary>
    private void RollbackOpenChanges()
    {
        _pager.RollbackTransaction();
        _catalog.InvalidateTailHint(); // hints may point at pages the rollback undid
        foreach (var table in _tables.Values)
        {
            table.Root.PageId = table.SavedRoot;
            table.EntryCount = table.SavedCount;
            table.Tree.InvalidateTailHint();
        }
    }

    // ---- Explicit transactions (one at a time, spanning tables) -------------

    /// <summary>
    /// Opens a transaction spanning any number of operations on any tables. The calling thread
    /// holds the write lock until <see cref="CommitTransaction"/> or
    /// <see cref="CancelTransaction"/>: reads and writes from this thread see the transaction's
    /// own uncommitted state; every other thread blocks. Only one transaction can be open at a
    /// time — a second caller blocks here until the first ends.
    /// </summary>
    public void StartTransaction()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (OnTransactionThread)
            throw new InvalidOperationException("A transaction is already open on this thread.");

        _lock.EnterWriteLock();
        _pager.BeginTransaction();
        Volatile.Write(ref _txnThreadId, Environment.CurrentManagedThreadId);
    }

    /// <summary>
    /// Commits the open transaction, stamping <paramref name="timestamp"/> into the meta page
    /// (see <see cref="CommitTimestamp"/>) in the same atomic commit. Durability follows the
    /// engine's <see cref="FlushMode"/>, exactly like a batch.
    /// </summary>
    public void CommitTransaction(long timestamp)
    {
        RequireOpenTransaction();

        bool mustFlush = _flushMode == FlushMode.Sync;
        long seq;
        try
        {
            _pager.CommitTimestamp = timestamp;
            // Dirty the meta page so a timestamp-only transaction (no data writes) still reaches
            // the flush snapshot — PrepareFlush refreshes its content from the meta fields, and
            // the WAL's diffing skips it entirely when nothing actually changed.
            _pager.GetWritable(Pager.MetaPageId);
            seq = FinishCommit(mustFlush);
        }
        catch
        {
            RollbackOpenChanges();
            throw;
        }
        finally
        {
            Volatile.Write(ref _txnThreadId, -1);
            _lock.ExitWriteLock();
        }

        if (mustFlush) WaitDurable(seq);
    }

    /// <summary>Discards every write made since <see cref="StartTransaction"/>.</summary>
    public void CancelTransaction()
    {
        RequireOpenTransaction();
        try
        {
            RollbackOpenChanges();
        }
        finally
        {
            Volatile.Write(ref _txnThreadId, -1);
            _lock.ExitWriteLock();
        }
    }

    private void RequireOpenTransaction()
    {
        if (!OnTransactionThread)
            throw new InvalidOperationException("No transaction is open on this thread.");
    }

    /// <summary>The timestamp passed to the last committed <see cref="CommitTransaction"/> (0 for
    /// a fresh file). Persisted in the meta page, so it is readable immediately after opening and
    /// crash-consistent with the data that commit wrote.</summary>
    public long CommitTimestamp
    {
        get
        {
            bool locked = EnterReadIfNeeded();
            try { return _pager.CommitTimestamp; }
            finally { ExitReadIfNeeded(locked); }
        }
    }

    // ---- Durability ---------------------------------------------------------

    /// <summary>Blocks until commit <paramref name="seq"/> is durable, flushing as the leader if
    /// no in-flight flush will cover it. A commit staged before a leader's snapshot is covered by
    /// that leader's single fsync (group commit); at worst one extra round is needed.</summary>
    private void WaitDurable(long seq)
    {
        while (Volatile.Read(ref _durableSeq) < seq)
        {
            lock (_flushLock)
            {
                if (Volatile.Read(ref _durableSeq) >= seq) return;
                FlushAsLeader();
            }
        }
    }

    // Must be called holding _flushLock: snapshots the staged set under the write lock (brief),
    // then does the WAL append + fsync without it.
    private void FlushAsLeader()
    {
        long target;
        _lock.EnterWriteLock();
        try
        {
            target = _commitSeq;
            WriteBackCatalogRecords(); // folded into the snapshot below, atomically with the data
            _pager.PrepareFlush();
        }
        finally { _lock.ExitWriteLock(); }

        _pager.CommitFlush();
        Volatile.Write(ref _durableSeq, target);
    }

    /// <summary>
    /// Releases the engine's in-memory page cache and buffer pool. Only re-readable (main-file
    /// durable) pages are dropped — unflushed and un-checkpointed pages are retained, so this
    /// never affects durability. Subsequent reads repopulate the cache from disk.
    /// </summary>
    public void ClearCache()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool locked = !OnTransactionThread;
        if (locked) _lock.EnterWriteLock();
        try { _pager.ClearCache(); }
        finally { if (locked) _lock.ExitWriteLock(); }
    }

    /// <summary>
    /// Forces every staged-but-unflushed batch/transaction to disk now (WAL write + fsync, then
    /// apply). A no-op when nothing is pending — e.g. in <see cref="FlushMode.Sync"/> mode, where
    /// each commit already flushed. Serialises with other flushes; writers only block for the
    /// brief staged-set snapshot, not the fsync. Cannot run on a thread with an open transaction.
    /// </summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (OnTransactionThread)
            throw new InvalidOperationException("Cannot flush while a transaction is open on this thread.");
        lock (_flushLock) FlushAsLeader();
    }

    // ---- Per-operation write helpers (called under the write lock) ----------

    private bool InsertBytes(Table table, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        int size = Node.LeafEntrySize(key.Length, value.Length);
        if (size > MaxEntrySize)
            throw new KvStoreException(
                $"Entry too large: key={key.Length}B value={value.Length}B exceeds the {MaxEntrySize}B limit.");

        if (table.Tree.Insert(key, value)) { table.EntryCount++; return true; }
        return false;
    }

    private bool DeleteBytes(Table table, ReadOnlySpan<byte> key)
    {
        if (table.Tree.Delete(key)) { table.EntryCount--; return true; }
        return false;
    }

    private byte[]? GetInBatch(Table table, ReadOnlySpan<byte> key)
        => table.Tree.TryGet(key, out var v) ? v : null;

    // ---- Background flushing ----------------------------------------------

    private void FlushLoop()
    {
        // Wait returns true when signalled to stop, false on timeout (time to flush).
        while (!_stop.Wait(_flushDelayMs))
        {
            try
            {
                lock (_flushLock) FlushAsLeader();
            }
            catch (ObjectDisposedException) { break; }
        }
    }

    public void Dispose() => Shutdown(durable: true);

    /// <summary>Test hook: tears the engine down as a process crash would — no final flush, no
    /// checkpoint — so reopening the path exercises WAL recovery of exactly the committed state.</summary>
    internal void SimulateCrash() => Shutdown(durable: false);

    // The one teardown sequence; a "crash" is a shutdown that skips every durability step.
    // An open transaction on the calling thread is cancelled (its writes were never committed).
    private void Shutdown(bool durable)
    {
        if (_disposed) return;
        if (OnTransactionThread) CancelTransaction();
        _disposed = true;

        if (_flusher is not null)
        {
            _stop.Set();
            _flusher.Join();
        }

        if (durable)
        {
            // Persist anything still staged so a clean shutdown loses nothing.
            lock (_flushLock) FlushAsLeader();
            _pager.Dispose();
        }
        else
        {
            _pager.SimulateCrash();
        }

        _lock.Dispose();
        _stop.Dispose();
    }

    /// <summary>
    /// The set of mutations applied within one <see cref="Batch"/> (or as part of an open
    /// transaction). Reads inside observe the batch's own uncommitted writes.
    /// </summary>
    public sealed class ByteWriteBatch
    {
        private readonly StorageEngine _engine;
        private readonly Table _table;

        internal ByteWriteBatch(StorageEngine engine, Table table)
        {
            _engine = engine;
            _table = table;
        }

        public bool Put(byte[] key, byte[] value)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(value);
            return _engine.InsertBytes(_table, key, value);
        }

        /// <summary>Span overload: the engine copies both spans into the page, so callers may
        /// encode into stack buffers and skip the per-put arrays.</summary>
        public bool Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value) => _engine.InsertBytes(_table, key, value);

        public bool Delete(byte[] key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _engine.DeleteBytes(_table, key);
        }

        /// <summary>Span overload; see the span <see cref="Put(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>.</summary>
        public bool Delete(ReadOnlySpan<byte> key) => _engine.DeleteBytes(_table, key);

        public byte[]? Get(byte[] key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _engine.GetInBatch(_table, key);
        }

        /// <summary>Span overload; see the span <see cref="Put(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>.</summary>
        public byte[]? Get(ReadOnlySpan<byte> key) => _engine.GetInBatch(_table, key);
    }
}
