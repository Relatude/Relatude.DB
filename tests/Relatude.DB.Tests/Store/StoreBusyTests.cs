using System.Diagnostics;
using System.Reflection;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.Utils;

namespace Relatude.Store;

/// <summary>
/// A transaction holds the store's write lock for as long as it runs, and a query waits for it. The wait
/// used to be one EnterReadLock that nothing could end: not the caller's cancellation, not the query time
/// limit. These tests hold the write lock the way a long transaction does and check how a query waits:
/// for as long as it may, until it is stopped, or - with a lock-wait budget - until it gives up as busy.
/// </summary>
[TestClass]
public class StoreBusyTests {
    static readonly TimeSpan wait = TimeSpan.FromSeconds(10);

    static NodeStore open(Action<SettingsLocal>? configure = null) {
        var settings = new SettingsLocal { AutoFlushDiskInBackground = false };
        configure?.Invoke(settings);
        var store = new NodeStore(DataStoreLocal.Open(Helper.GetDatamodel(), settings, new IOProviderMemory()));
        store.Insert(Enumerable.Range(1, 20).Select(i => (object)new Article { Id = i, Name = "Article " + i, Body = "body" }).ToList());
        return store;
    }
    // holds the store's write lock on a thread of its own, as a running transaction does, until the returned action is called
    static Action holdWriteLock(NodeStore store) {
        var rw = (ReaderWriterLockSlim)typeof(DataStoreLocal).GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store.Datastore)!;
        var held = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var thread = new Thread(() => {
            rw.EnterWriteLock();
            held.Set();
            release.Wait();
            rw.ExitWriteLock();
        }) { IsBackground = true };
        thread.Start();
        Assert.IsTrue(held.Wait(wait));
        return () => {
            release.Set();
            thread.Join();
        };
    }
    static QueryContext admin => QueryContext.Default.Admin();

    [TestMethod]
    public void AQueryWithALockWaitGivesUpAsBusy() {
        using var store = open();
        var release = holdWriteLock(store);
        try {
            var sw = Stopwatch.StartNew();
            var busy = Assert.Throws<StoreBusyException>(() => store.Query<Article>(admin.WithLockWait(TimeSpan.FromMilliseconds(100))).Count());
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(5), "gave up after " + sw.Elapsed);
            Assert.IsTrue(busy.Waited >= TimeSpan.FromMilliseconds(90), "waited only " + busy.Waited);
            // zero does not wait at all, and a single read gives up the same way
            Assert.Throws<StoreBusyException>(() => store.Query<Article>(admin.WithLockWait(TimeSpan.Zero)).Count());
            Assert.Throws<StoreBusyException>(() => store.Datastore.Get(1, admin.WithLockWait(TimeSpan.Zero)));
        } finally {
            release();
        }
        // with the store free again, the same context counts as usual
        Assert.AreEqual(20, store.Query<Article>(admin.WithLockWait(TimeSpan.Zero)).Count());
    }

    [TestMethod]
    public void ACancelledQueryStopsWaitingForTheLock() {
        using var store = open();
        var release = holdWriteLock(store);
        try {
            using var cts = new CancellationTokenSource();
            var query = Task.Run(() => store.Query<Article>(admin.WithCancellation(cts.Token)).Count());
            Assert.IsFalse(query.Wait(300), "the query ran while the write lock was held");
            cts.Cancel();
            var error = Assert.Throws<AggregateException>(() => query.Wait(wait));
            Assert.IsInstanceOfType<OperationCanceledException>(error.InnerException);
        } finally {
            release();
        }
    }

    [TestMethod]
    public void TheQueryTimeLimitCountsTheWait_AndSaysWhatItWaitedFor() {
        using var store = open(s => s.MaxQueryDurationInSeconds = 0.3);
        var release = holdWriteLock(store);
        try {
            var query = Task.Run(() => store.Query<Article>(admin).Count());
            var error = Assert.Throws<AggregateException>(() => query.Wait(wait));
            Assert.IsInstanceOfType<TimeoutException>(error.InnerException);
            Assert.IsNotInstanceOfType<StoreBusyException>(error.InnerException);
            StringAssert.Contains(error.InnerException!.Message, "waited for a transaction to finish");
        } finally {
            release();
        }
    }

    [TestMethod]
    public void AQueryWithoutLimitsWaitsAndThenRuns() {
        using var store = open();
        var release = holdWriteLock(store);
        Task<int> query;
        try {
            query = Task.Run(() => store.Query<Article>(admin).Count());
            Assert.IsFalse(query.Wait(300), "the query ran while the write lock was held");
        } finally {
            release();
        }
        Assert.IsTrue(query.Wait(wait));
        Assert.AreEqual(20, query.Result);
    }

    [TestMethod]
    public void WhatAStatusPageReadsAnswersWhileATransactionHoldsTheStore() {
        using var store = open();
        var timestamp = store.Datastore.GetLastTimestampID();
        var release = holdWriteLock(store);
        try {
            var waiting = Task.Run(() => store.Query<Article>(admin).Count());
            Assert.IsFalse(waiting.Wait(200));
            var answers = Task.Run(() => {
                Assert.AreEqual(timestamp, store.Datastore.PeekLastTimestamp());
                Assert.AreEqual(20, store.Datastore.PeekCounters().NodeCount);
                return store.Datastore.GetStatus();
            });
            Assert.IsTrue(answers.Wait(wait), "a lock-free read waited for the write lock");
            Assert.IsTrue(answers.Result.ReadersWaiting >= 1, "the waiting query is counted");
        } finally {
            release();
        }
    }
}
