using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.Utils;

namespace Relatude.Store;

/// <summary>
/// A query that has to read many nodes from the log must not hold up writers, nor every reader queued
/// behind them: the nodes are looked up under the store's read lock and read from the log after it is let
/// go (NodeStore.NodeRead, LogReadGate). These tests hold a query in the middle of its log read - a test
/// provider parks the read until it is let go - and check what may and may not happen meanwhile.
/// </summary>
[TestClass]
public class QueryConcurrencyTests {

    static readonly TimeSpan wait = TimeSpan.FromSeconds(10);
    static readonly AsyncLocal<bool> holdReadsHere = new(); // flows into the parallel batch reads of the same query

    /// <summary>A provider whose log reads park while a test holds them - only the reads made by the code
    /// that set <see cref="holdReadsHere"/>, so a writer or a rewrite on another thread reads freely.</summary>
    sealed class GatedIO(IIOProvider inner) : IIOProvider {
        public volatile bool Hold;
        public readonly ManualResetEventSlim ReadParked = new(false);
        public readonly ManualResetEventSlim Release = new(false);
        public int Reads;
        public void Park() {
            Interlocked.Increment(ref Reads);
            if (!Hold || !holdReadsHere.Value) return;
            ReadParked.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("The test never let the read go. ");
        }
        public IReadStream OpenRead(string[] path, long position) => inner.OpenRead(path, position);
        public IAppendStream OpenAppend(string[] path) => new GatedStream(this, inner.OpenAppend(path));
        public bool Exists(string[] path) => inner.Exists(path);
        public bool DoesNotExistOrIsEmpty(string[] path) => inner.DoesNotExistOrIsEmpty(path);
        public void DeleteFileIfItExists(string[] path) => inner.DeleteFileIfItExists(path);
        public FileMeta[] GetFiles() => inner.GetFiles();
        public long GetFileSizeOrZeroIfUnknown(string[] path) => inner.GetFileSizeOrZeroIfUnknown(path);
        public bool CanRenameFile => inner.CanRenameFile;
        public void RenameFile(string[] path, string[] newPath) => inner.RenameFile(path, newPath);
        public bool CanRenameFolder => inner.CanRenameFolder;
        public void RenameFolder(string[] path, string[] newPath) => inner.RenameFolder(path, newPath);
        public bool SupportsEmptyFolders => inner.SupportsEmptyFolders;
        public bool CanTruncate => inner.CanTruncate;
        public void TruncateFile(string[] path, long newLength) => inner.TruncateFile(path, newLength);
        public void CloseAllOpenStreams() => inner.CloseAllOpenStreams();
        public bool TryGetLocalFilePath(string[] path, [MaybeNullWhen(false)] out string localFilePath) => inner.TryGetLocalFilePath(path, out localFilePath);
        public bool TryGetLocalFolderPath(string[] path, [MaybeNullWhen(false)] out string localFolderPath) => inner.TryGetLocalFolderPath(path, out localFolderPath);
        public bool TryMoveIfSameDrive(string fromLocalFilePath, string[] destination) => inner.TryMoveIfSameDrive(fromLocalFilePath, destination);
        public void DeleteFolderIfItExists(string[] path) => inner.DeleteFolderIfItExists(path);
        public bool DeleteFolderIfEmpty(string[] path) => inner.DeleteFolderIfEmpty(path);
        public void EnsureFolder(string[] path) => inner.EnsureFolder(path);
        public Task<FolderMeta> GetFolderAsync(string[] path, bool recursive, bool withFiles) => inner.GetFolderAsync(path, recursive, withFiles);
    }
    sealed class GatedStream(GatedIO io, IAppendStream inner) : IAppendStream {
        public string FileKey => inner.FileKey;
        public long Length => inner.Length;
        public void Append(byte[] data) => inner.Append(data);
        public void Append(byte[] data, int count) => inner.Append(data, count);
        public void RecordChecksum() => inner.RecordChecksum();
        public void WriteChecksum() => inner.WriteChecksum();
        public void Flush(bool deepFlush) => inner.Flush(deepFlush);
        public void Get(long position, int count, byte[] buffer) {
            io.Park();
            inner.Get(position, count, buffer);
        }
        public void ResetByteCounter() => inner.ResetByteCounter();
        public long GetBytesRead() => inner.GetBytesRead();
        public long GetBytesWritten() => inner.GetBytesWritten();
        public Task AppendAsyncNoChecksumOrLock(byte[] buffer, int count) => inner.AppendAsyncNoChecksumOrLock(buffer, count);
        public void Dispose() => inner.Dispose();
    }

    // big enough that the log holds more than one 10 MB read batch
    const int articleCount = 1000;
    static readonly string body = string.Concat(Enumerable.Repeat("0123456789", 1200)); // 12 KB a node

    static NodeStore open(GatedIO io, double nodeCacheGb = 0.01, Action<SettingsLocal>? configure = null) {
        var settings = new SettingsLocal { NodeCacheSizeGb = nodeCacheGb, AutoFlushDiskInBackground = false };
        configure?.Invoke(settings);
        return new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel(), settings, io));
    }
    static void fill(NodeStore store, int count = articleCount) {
        store.Insert(Enumerable.Range(1, count).Select(i => (object)new Article { Id = i, Name = "Article " + i, Body = body, IntegerNum = i % 10 }).ToList(), flushToDisk: true);
        store.Maintenance(MaintenanceAction.ClearCache); // every node has to come from the log
    }
    static DataStoreLocal local(NodeStore store) => (DataStoreLocal)store.Datastore;
    // starts the query on a thread of its own with its log reads parked, and waits until it is in them
    static Task<T> parkedQuery<T>(GatedIO io, Func<T> query) {
        io.Hold = true;
        var task = Task.Run(() => {
            holdReadsHere.Value = true;
            return query();
        });
        if (!io.ReadParked.Wait(wait)) throw new AssertFailedException("The query never reached the log. " + (task.Exception?.ToString() ?? ""));
        return task;
    }
    static void letGo(GatedIO io) {
        io.Hold = false;
        io.Release.Set();
    }

    [TestMethod]
    public void AWriterIsNotHeldUpByAQueryReadingTheLog() {
        var io = new GatedIO(new IOProviderMemory());
        using var store = open(io);
        fill(store);
        var query = parkedQuery(io, () => store.Query<Article>().Execute().ToList());
        try {
            // the query is in its log read; before, it held the read lock through it and this waited for the disk
            var write = Task.Run(() => store.Insert(new Article { Id = articleCount + 1, Name = "written while reading", Body = "x" }));
            Assert.IsTrue(write.Wait(wait), "the write waited for the query's disk read");
            var count = Task.Run(() => store.Query<Article>().Count());
            Assert.IsTrue(count.Wait(wait), "a query arriving after the write waited too");
            Assert.AreEqual(articleCount + 1, count.Result);
        } finally {
            letGo(io);
        }
        var result = query.Result;
        // the query's answer is the state when it ran: the node written during its read is not in it
        Assert.AreEqual(articleCount, result.Count);
        Assert.IsTrue(result.All(a => a.Body == body));
        CollectionAssert.AreEquivalent(Enumerable.Range(1, articleCount).Select(i => "Article " + i).ToList(), result.Select(a => a.Name).ToList());
    }

    [TestMethod]
    public void TheResultIsTheStateTheQueryRanIn_AndNoStaleVersionIsCached() {
        var io = new GatedIO(new IOProviderMemory());
        using var store = open(io);
        fill(store);
        var query = parkedQuery(io, () => store.Query<Article>().Execute().ToList());
        try {
            var changes = Task.Run(() => {
                var changed = store.Get<Article>(7);
                changed.Name = "changed";
                store.Update(changed, flushToDisk: true);
                store.Delete(store.Get<Article>(8), flushToDisk: true);
                store.Maintenance(MaintenanceAction.ClearCache); // the new version of 7 is read from the log again from here
            });
            Assert.IsTrue(changes.Wait(wait), "the writes waited for the query's disk read");
        } finally {
            letGo(io);
        }
        var result = query.Result;
        Assert.AreEqual("Article 7", result.Single(a => a.Id == 7).Name, "the query sees 7 as it was when it ran");
        Assert.IsTrue(result.Any(a => a.Id == 8), "and 8, deleted while it read, is in its answer");
        // what the query read must not have gone into the cache as the current version
        Assert.AreEqual("changed", store.Get<Article>(7).Name);
        Assert.IsFalse(store.Datastore.Exists(8));
        Assert.AreEqual(articleCount - 1, store.Query<Article>().Count());
    }

    [TestMethod]
    public void ARewriteWaitsForReadsInFlight_ThenSwapsTheFile() {
        var io = new GatedIO(new IOProviderMemory());
        using var store = open(io);
        fill(store);
        // a second version of every node, so the rewritten file is a different layout: a position from
        // the old file read in the new one would hand back the wrong bytes
        foreach (var a in store.Query<Article>().Execute().ToList()) {
            a.Name += " v2";
            store.Update(a);
        }
        store.Maintenance(MaintenanceAction.FlushDisk | MaintenanceAction.ClearCache);
        var db = local(store);
        var query = parkedQuery(io, () => store.Query<Article>().Execute().ToList());
        Task rewrite;
        try {
            rewrite = Task.Run(() => db.RewriteStore(true, FileKeyUtility.WAL_NextFileKey(io)));
            // the swap needs the reader's lease back, and the reader is parked
            Assert.IsFalse(rewrite.Wait(TimeSpan.FromSeconds(2)), "the log file was swapped under a read in flight");
            Assert.IsTrue(db._wal.ReadLeasesOut > 0, "the parked read holds a lease");
        } finally {
            letGo(io);
        }
        Assert.IsTrue(rewrite.Wait(wait), "the rewrite did not finish once the read was done");
        var result = query.Result;
        Assert.AreEqual(articleCount, result.Count);
        Assert.IsTrue(result.All(a => a.Name.EndsWith(" v2") && a.Body == body));
        // and the swapped file reads back the same
        store.Maintenance(MaintenanceAction.ClearCache);
        var after = store.Query<Article>().Execute().ToList();
        Assert.AreEqual(articleCount, after.Count);
        Assert.IsTrue(after.All(a => a.Name.EndsWith(" v2") && a.Body == body));
        Assert.AreEqual(0, db._wal.ReadLeasesOut);
    }

    [TestMethod]
    public void CancellingTheContextStopsAQueryInItsLogRead() {
        var io = new GatedIO(new IOProviderMemory());
        using var store = open(io);
        fill(store);
        using var cancel = new CancellationTokenSource();
        var ctx = QueryContext.Default.WithCancellation(cancel.Token);
        var query = parkedQuery(io, () => store.Datastore.Query("Article", [], ctx));
        cancel.Cancel();
        letGo(io); // the batch in hand finishes; the next one is not read
        var error = Assert.Throws<AggregateException>(() => query.Wait(wait));
        Assert.IsInstanceOfType<OperationCanceledException>(error.InnerException);
        Assert.AreEqual(0, local(store)._wal.ReadLeasesOut, "a cancelled read gives its lease back");
        Assert.AreEqual(articleCount, store.Query<Article>().Count(), "and the store goes on as before");
    }

    [TestMethod]
    public void ACancelledTokenStopsTheQueryBeforeItStarts() {
        var io = new GatedIO(new IOProviderMemory());
        using var store = open(io);
        fill(store, 10);
        var ctx = QueryContext.Default.WithCancellation(new CancellationToken(true));
        Assert.Throws<OperationCanceledException>(() => store.Datastore.Query("Article", [], ctx));
    }

    [TestMethod]
    public void CancellingStopsARowFilterReadingTheLog() {
        // Body is not indexed, so Contains on it is evaluated row by row over nodes read from the log
        var io = new GatedIO(new IOProviderMemory());
        using var store = open(io);
        fill(store);
        using var cancel = new CancellationTokenSource();
        var ctx = QueryContext.Default.WithCancellation(cancel.Token);
        var text = store.Query<Article>().Where("a => a.Body.Contains(\"9\")").ToString()!;
        var query = parkedQuery(io, () => store.Datastore.Query(text, [], ctx));
        cancel.Cancel();
        letGo(io);
        var error = Assert.Throws<AggregateException>(() => query.Wait(wait));
        Assert.IsInstanceOfType<OperationCanceledException>(error.InnerException);
    }

    [TestMethod]
    public void AQueryOverItsTimeLimitIsStopped() {
        var io = new GatedIO(new IOProviderMemory());
        using var store = open(io, configure: s => s.MaxQueryDurationInSeconds = 0.3);
        fill(store);
        var query = parkedQuery(io, () => store.Datastore.Query("Article", []));
        Thread.Sleep(600); // past the limit while parked in the first batch
        letGo(io);
        var error = Assert.Throws<AggregateException>(() => query.Wait(wait));
        Assert.IsInstanceOfType<TimeoutException>(error.InnerException);
        StringAssert.Contains(error.InnerException!.Message, "MaxQueryDurationInSeconds");
        // a quick query under the same limit is not affected
        Assert.AreEqual(articleCount, store.Query<Article>().Count());
    }

    [TestMethod]
    public void AReadLargerThanTheCacheReadsEachNodeOnce_AndLeavesTheWorkingSetCached() {
        var io = new GatedIO(new IOProviderMemory());
        using var store = open(io, nodeCacheGb: 2.0 / 1024); // 2 MB: a sixth of what the query reads
        fill(store);
        // a working set read before the scan
        var hot = Enumerable.Range(1, 20).ToArray();
        foreach (var id in hot) store.Get<Article>(id);
        var readsBefore = io.Reads;
        var all = store.Query<Article>().Execute().ToList();
        Assert.AreEqual(articleCount, all.Count);
        // read in batches - a dozen calls at most, not one per node as when the batch was pushed out of
        // the cache before it was taken back from it
        Assert.IsTrue(io.Reads - readsBefore < 20, "the scan made " + (io.Reads - readsBefore) + " reads");
        // the scan went into the cache cold: what was used before it is still there
        var readsAfterScan = io.Reads;
        foreach (var id in hot) store.Get<Article>(id);
        Assert.AreEqual(readsAfterScan, io.Reads, "the working set was pushed out of the cache by the scan");
    }

    [TestMethod]
    public void NodeGetsReadOutsideTheLock_TooManyAndOne() {
        var io = new GatedIO(new IOProviderMemory());
        using var store = open(io);
        fill(store);
        var guids = store.Query<Article>().Execute().Select(a => a.PId).ToArray();
        store.Maintenance(MaintenanceAction.ClearCache);
        // a bulk get parked in its read does not hold up a writer either
        var get = parkedQuery(io, () => store.Datastore.Get(guids).ToList());
        try {
            var write = Task.Run(() => store.Insert(new Article { Id = articleCount + 1, Name = "x", Body = "x" }));
            Assert.IsTrue(write.Wait(wait), "the write waited for the bulk get's disk read");
        } finally {
            letGo(io);
        }
        Assert.AreEqual(articleCount, get.Result.Count);
        // single gets: found, missing, and a guid nobody knows
        store.Maintenance(MaintenanceAction.ClearCache);
        Assert.IsTrue(store.Datastore.TryGet(5, out var five));
        Assert.AreEqual(5, five!.__Id);
        Assert.IsFalse(store.Datastore.TryGet(articleCount + 50, out _));
        Assert.IsFalse(store.Datastore.TryGet(Guid.NewGuid(), out _));
        Assert.Throws<KeyNotFoundException>(() => store.Datastore.Get(articleCount + 50));
        Assert.AreEqual(0, local(store)._wal.ReadLeasesOut);
    }

    // ---- the background flush: never in the queue for the write lock behind a reader ----

    static object storeLock(DataStoreLocal db) => typeof(DataStoreLocal).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(db)!;
    // holds the store's read lock on a thread of its own until the returned action is called
    static Action holdReadLock(DataStoreLocal db) {
        var rw = (ReaderWriterLockSlim)storeLock(db);
        var held = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var thread = new Thread(() => {
            rw.EnterReadLock();
            held.Set();
            release.Wait();
            rw.ExitReadLock();
        }) { IsBackground = true };
        thread.Start();
        Assert.IsTrue(held.Wait(wait));
        return () => {
            release.Set();
            thread.Join();
        };
    }

    [TestMethod]
    public void TheBackgroundFlushDoesNotQueueBehindAReader() {
        var folder = Path.Combine(Path.GetTempPath(), "relatude-bgflush-" + Guid.NewGuid().ToString("N"));
        try {
            var settings = TestEngines.Settings(value: IndexEngineTypes.Native); // a persisted engine: the flush has a locked part
            settings.AutoFlushDiskInBackground = false;
            using var store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel(), settings, new IOProviderDisk(folder), createIndexEngines: TestEngines.Factory(folder, settings)));
            var db = local(store);
            store.Insert(new Article { Id = 1, Name = "a", Body = "b" });
            var release = holdReadLock(db);
            try {
                // engines are behind the log, but the write lock is taken: the background flush leaves them for the next tick
                var flush = Task.Run(() => db.FlushToDisk(true, 0, background: true, out _, out _, out _));
                Assert.IsTrue(flush.Wait(wait), "the background flush waited for the reader");
                // and no writer was left waiting, so a new reader gets in
                var read = Task.Run(() => store.Query<Article>().Count());
                Assert.IsTrue(read.Wait(wait), "a reader was held up behind the background flush");
            } finally {
                release();
            }
            db.FlushToDisk(true, 0); // the engines are made durable now
            // with nothing new to make durable, even an ordinary flush does not ask for the write lock
            release = holdReadLock(db);
            try {
                var flush = Task.Run(() => db.FlushToDisk(true, 0));
                Assert.IsTrue(flush.Wait(wait), "a flush with nothing to make durable waited for the write lock");
            } finally {
                release();
            }
        } finally {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    [TestMethod]
    public void HousekeepingCountersAndStatusDoNotTakeTheWriteLock() {
        var io = new GatedIO(new IOProviderMemory());
        using var store = open(io);
        fill(store, 10);
        var db = local(store);
        var release = holdReadLock(db);
        try {
            var calls = Task.Run(() => {
                db.GetNoPrimitiveActionsInLogThatCanBeTruncated();
                db.GetLogActionsNotItInStatefile();
                return db.GetInfo();
            });
            Assert.IsTrue(calls.Wait(wait), "a counter or the status waited for the write lock");
            Assert.IsTrue(calls.Result.IsFresh, "the status could be read beside a reader");
            Assert.IsFalse(db.TryPurgeCachesInBackground(), "the background purge took its turn while a reader was in");
        } finally {
            release();
        }
        Assert.IsTrue(db.TryPurgeCachesInBackground());
    }
}
