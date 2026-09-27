using Microsoft.Data.Sqlite;
using Relatude.DB.AI;
using Relatude.DB.DataStores;
using Relatude.DB.Tasks;
using Relatude.DB.Tasks.TextIndexing;
using Relatude.Utils;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Relatude.Store;

/// <summary>
/// A queue runs batches side by side, as many of one type as the type allows: what its runner asks
/// for, or what the settings say for it, never past the runner's own limit. Two batches of a type
/// holding the same concurrency key never overlap, and neither does a batch with itself.
///
/// The probe runner below holds every batch at a gate the test opens, so what runs at the same time
/// is decided by the test rather than by how fast a machine happens to be.
/// </summary>
[TestClass]
public class TaskQueueConcurrencyTests {

    [TestMethod]
    public async Task RunsAsManyBatchesAtOnceAsTheRunnerAsksFor() {
        var probe = new Probe();
        var runner = new ConcurrencyProbeRunner(probe) { Concurrency = 3 };
        var store = open(runner);
        try {
            enqueue(store, probe, 0, 1, 2, 3, 4, 5);
            var run = store.TaskQueue.ExecuteTasksAsync(60_000, () => false, 0);
            waitUntil(() => probe.Current == 3, "three batches running");
            settle();
            Assert.AreEqual(3, probe.Current, "no more than three at once");
            Assert.AreEqual(3, store.TaskQueue.RunningCount(runner.TaskTypeId));
            Assert.AreEqual(3, store.TaskQueue.CountBatch(BatchState.Running));
            probe.ReleaseAll();
            var results = await run;
            Assert.AreEqual(6, results.Length);
            Assert.IsTrue(results.All(r => r.Error == null));
            Assert.AreEqual(3, probe.Max);
            Assert.AreEqual(0, store.TaskQueue.RunningCount(runner.TaskTypeId));
            Assert.AreEqual(0, store.TaskQueue.CountBatch(BatchState.Pending));
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public async Task OneAtATimeUnlessTheRunnerSaysOtherwise() {
        var probe = new Probe();
        var runner = new ConcurrencyProbeRunner(probe); // MaxConcurrency left at the default
        var store = open(runner);
        try {
            Assert.AreEqual(1, store.TaskQueue.ConcurrencyOf(runner));
            enqueue(store, probe, 0, 1, 2);
            var run = store.TaskQueue.ExecuteTasksAsync(60_000, () => false, 0);
            waitUntil(() => probe.Current == 1, "a batch running");
            settle();
            Assert.AreEqual(1, probe.Current);
            probe.ReleaseAll();
            Assert.AreEqual(3, (await run).Length);
            Assert.AreEqual(1, probe.Max);
        } finally {
            store.Dispose();
        }
    }

    /// <summary>The settings decide over the runner, and they are read every time a batch is about to
    /// start - so raising the number mid-run fills the slots that open next.</summary>
    [TestMethod]
    public async Task TheSettingDecidesAndAppliesToTheNextBatchThatStarts() {
        var probe = new Probe();
        var runner = new ConcurrencyProbeRunner(probe) { Concurrency = 4 };
        var entry = new TaskConcurrencySettings { Id = Guid.NewGuid(), TaskType = nameof(ConcurrencyProbeTask), MaxConcurrency = 2 };
        var store = open(runner, new SettingsLocal { TaskConcurrency = [entry] });
        try {
            Assert.AreEqual(2, store.TaskQueue.ConcurrencyOf(runner));
            Assert.AreEqual(2, store.TaskQueue.ConfiguredConcurrencyOf(runner));
            enqueue(store, probe, 0, 1, 2, 3, 4, 5);
            var run = store.TaskQueue.ExecuteTasksAsync(60_000, () => false, 0);
            waitUntil(() => probe.Current == 2, "two batches running");
            settle();
            Assert.AreEqual(2, probe.Current, "the setting, not the runner's four");

            entry.MaxConcurrency = 5; // the same object the admin UI edits
            probe.Release(probe.ActiveKeys().First());
            // one finished, five left, and room for all of them now
            waitUntil(() => probe.Current == 5, "five batches running after the change");

            probe.ReleaseAll();
            Assert.AreEqual(6, (await run).Length);
            Assert.AreEqual(5, probe.Max);

            // cleared, the runner's own number is back
            entry.MaxConcurrency = null;
            Assert.AreEqual(4, store.TaskQueue.ConcurrencyOf(runner));
            Assert.IsNull(store.TaskQueue.ConfiguredConcurrencyOf(runner));
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public void TheRunnersLimitCapsTheSetting() {
        var probe = new Probe();
        var runner = new ConcurrencyProbeRunner(probe) { Concurrency = 1, Limit = 1 };
        var settings = new SettingsLocal {
            TaskConcurrency = [
                new() { Id = Guid.NewGuid(), TaskType = typeof(ConcurrencyProbeTask).FullName, MaxConcurrency = 5 },
                new() { Id = Guid.NewGuid(), TaskType = typeof(RewriteTask).FullName, MaxConcurrency = 3 },
                new() { Id = Guid.NewGuid(), TaskType = typeof(TextIndexTask).FullName, MaxConcurrency = 1000 },
            ],
        };
        var store = open(runner, settings);
        try {
            Assert.AreEqual(1, store.TaskQueue.ConcurrencyOf(runner));
            // a log rewrite refuses to start beside another, so it stays at one whatever is set
            var rewrite = store.TaskQueue.Runners.Single(r => r is RewriteTaskRunner);
            Assert.AreEqual(1, store.TaskQueue.ConcurrencyOf(rewrite));
            Assert.AreEqual(1, TaskQueue.ConcurrencyLimitOf(rewrite));
            // and nothing runs more than the queue supports
            var text = store.TaskQueue.Runners.Single(r => r is TextIndexTaskRunner);
            Assert.AreEqual(TaskQueue.MaxSupportedConcurrency, store.TaskQueue.ConcurrencyOf(text));
            // below one is one: zero would stop the type rather than slow it down
            settings.TaskConcurrency[2].MaxConcurrency = 0;
            Assert.AreEqual(1, store.TaskQueue.ConcurrencyOf(text));
        } finally {
            store.Dispose();
        }
    }

    [TestMethod]
    public void TheBuiltInTypesRunAsTheirWorkSuits() {
        var store = new DataStoreLocal(Helper.GetDatamodel(), new SettingsLocal { AutoDequeTasks = false }, ai: AIEngine.CreateDummy());
        store.Open();
        try {
            var runners = store.TaskQueue.Runners.ToArray();
            // embedding mostly waits for the AI service, outside every lock
            Assert.AreEqual(4, store.TaskQueue.ConcurrencyOf(runners.Single(r => r is SemanticIndexTaskRunner)));
            // text indexing is written under the database's write lock anyway
            Assert.AreEqual(1, store.TaskQueue.ConcurrencyOf(runners.Single(r => r is TextIndexTaskRunner)));
            Assert.AreEqual(1, store.TaskQueue.ConcurrencyOf(runners.Single(r => r is RewriteTaskRunner)));
            // both index types keep two batches off the same node
            Assert.AreEqual("7", runners.Single(r => r is SemanticIndexTaskRunner).GetConcurrencyKeyGeneric(new SemanticIndexTask(7)));
            Assert.AreEqual("7", runners.Single(r => r is TextIndexTaskRunner).GetConcurrencyKeyGeneric(new TextIndexTask(7)));
        } finally {
            store.Dispose();
        }
    }

    /// <summary>Two batches for the same node, finishing in the wrong order, would leave the older
    /// result in the index - so a batch whose key a running batch holds waits for it, while batches
    /// with other keys go past it.</summary>
    [TestMethod]
    public async Task BatchesSharingAKeyNeverOverlap() {
        var probe = new Probe();
        var runner = new ConcurrencyProbeRunner(probe) { Concurrency = 3, Keyed = true };
        var store = open(runner);
        try {
            enqueue(store, probe, 1, 1, 2);
            var run = store.TaskQueue.ExecuteTasksAsync(60_000, () => false, 0);
            waitUntil(() => probe.Current == 2, "the first batch of key 1 and the batch of key 2");
            settle();
            Assert.AreEqual(2, probe.Current, "the second batch of key 1 waits, although there is room");
            Assert.AreEqual(1, probe.Active(1));
            Assert.AreEqual(1, probe.Active(2));

            probe.Release(1); // the first of key 1 is done, so the second may start
            waitUntil(() => probe.Runs(1) == 2, "the second batch of key 1");
            probe.ReleaseAll();
            Assert.AreEqual(3, (await run).Length);
            Assert.AreEqual(1, probe.MaxPerKey);
        } finally {
            store.Dispose();
        }
    }

    /// <summary>
    /// "Queue again" on a batch that is running: it is not handed out again while that run lasts - not
    /// even to a free slot - and once the run is over it stays in line and runs a second time, which is
    /// what the admin UI says it will do. Before, the finishing run deleted it.
    /// </summary>
    [TestMethod]
    public async Task ABatchPutBackInLineWhileItRunsRunsAgainAfterwardsNeverBesideItself() {
        var probe = new Probe();
        var runner = new ConcurrencyProbeRunner(probe) { Concurrency = 2 }; // no keys: only the batch id keeps it apart
        var store = open(runner);
        try {
            enqueue(store, probe, "a", 1);
            enqueue(store, probe, "b", 2);
            var run = store.TaskQueue.ExecuteTasksAsync(60_000, () => false, 0);
            waitUntil(() => probe.Current == 2, "both batches running");
            var a = store.TaskQueue.GetBatchMeta([BatchState.Running], [], ["a"], 0, 10, out _).Single();
            store.TaskQueue.SetState([a.BatchId], BatchState.Pending);

            probe.Release(2); // b finishes and frees a slot, and a is pending - but still running
            waitUntil(() => probe.Runs(2) == 1 && probe.Active(2) == 0, "b done");
            settle();
            Assert.AreEqual(1, probe.Runs(1), "a was started beside its own first run");

            probe.Release(1); // a's first run ends; it is in line, so it runs again
            waitUntil(() => probe.Runs(1) == 2, "a's second run");
            probe.ReleaseAll();
            var results = await run;
            Assert.AreEqual(3, results.Length);
            Assert.AreEqual(1, probe.MaxPerKey);
            Assert.AreEqual(0, store.TaskQueue.CountBatch(BatchState.Pending));
            Assert.AreEqual(0, store.TaskQueue.CountBatch(BatchState.Running));
        } finally {
            store.Dispose();
        }
    }

    /// <summary>A batch deleted from the admin UI while it ran is gone when its failure comes back:
    /// that must not take the queue down with it, nor bring the batch back.</summary>
    [TestMethod]
    public async Task ABatchDeletedWhileItRunsCanStillFail() {
        var probe = new Probe();
        var runner = new ConcurrencyProbeRunner(probe) { Fail = true };
        var store = open(runner);
        try {
            enqueue(store, probe, "x", 1);
            enqueue(store, probe, "y", 2);
            var run = store.TaskQueue.ExecuteTasksAsync(60_000, () => false, 0);
            waitUntil(() => probe.Current == 1, "the first batch running");
            var running = store.TaskQueue.GetBatchMeta([BatchState.Running], [], [], 0, 10, out _).Single();
            store.TaskQueue.DeleteById([running.BatchId]);
            probe.ReleaseAll();
            var results = await run;
            Assert.AreEqual(2, results.Length);
            Assert.IsTrue(results.All(r => r.Error != null));
            // the other one failed and stays as failed; the deleted one is not brought back
            Assert.AreEqual(1, store.TaskQueue.CountBatch(BatchState.Failed));
            Assert.AreEqual(1, store.TaskQueue.GetBatchMeta([], [], [], 0, 10, out _).Length);
        } finally {
            store.Dispose();
        }
    }

    /// <summary>A shutdown waits for every batch still running, not only for the first to finish.</summary>
    [TestMethod]
    public async Task ShutdownWaitsForEveryRunningBatch() {
        var probe = new Probe();
        var runner = new ConcurrencyProbeRunner(probe) { Concurrency = 2 };
        var store = open(runner);
        try {
            enqueue(store, probe, 1, 2);
            var run = store.TaskQueue.ExecuteTasksAsync(60_000, () => false, 0);
            waitUntil(() => probe.Current == 2, "both batches running");
            probe.Release(1);
            waitUntil(() => probe.Active(1) == 0, "one of them done");
            Assert.IsFalse(store.TaskQueue.TryGracefulShutdown(100), "the other one is still running");
            probe.ReleaseAll();
            Assert.IsTrue(store.TaskQueue.TryGracefulShutdown(5000));
            Assert.AreEqual(0, probe.Current);
            Assert.AreEqual(2, (await run).Length);
            // and once shut down, nothing new starts
            store.EnqueueTask(new ConcurrencyProbeTask(9));
            Assert.AreEqual(0, (await store.TaskQueue.ExecuteTasksAsync(1000, () => false, 0)).Length);
        } finally {
            store.Dispose();
        }
    }

    /// <summary>The store hands out the best batch among those that can start: priority, then age,
    /// passing over the types that are full and the batches the queue turns down.</summary>
    [TestMethod]
    public void TheDefaultStorePassesOverWhatCannotStart() {
        var probe = new Probe();
        var low = new ConcurrencyProbeRunner(probe);
        var high = new HighPriorityProbeRunner();
        var runners = new Dictionary<string, ITaskRunner> { [low.TaskTypeId] = low, [high.TaskTypeId] = high };
        var queue = new DefaultQueueStore(runners);
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        var l1 = batch(low, new ConcurrencyProbeTask(1), t0);
        var l2 = batch(low, new ConcurrencyProbeTask(2), t0.AddMinutes(1));
        var h1 = batch(high, new HighPriorityProbeTask(3), t0.AddMinutes(2));
        foreach (var b in new[] { l1, l2, h1 }) queue.Enqueue(b, runners[b.Meta.TaskTypeId]);

        var none = new HashSet<string>();
        // the high priority type is full: the oldest low one, although the high one comes first
        Assert.AreEqual(l1.Meta.BatchId, queue.DequeueAndSetRunning(runners, new HashSet<string> { high.TaskTypeId }, null)!.Meta.BatchId);
        Assert.AreEqual(BatchState.Running, l1.Meta.State);
        // the head of the line turned down by the queue: the next one, not nothing
        Assert.AreEqual(l2.Meta.BatchId, queue.DequeueAndSetRunning(runners, none, b => b.Meta.BatchId != h1.Meta.BatchId)!.Meta.BatchId);
        Assert.IsNull(queue.DequeueAndSetRunning(runners, none, b => false));
        Assert.AreEqual(BatchState.Pending, h1.Meta.State, "a batch turned down stays in line");
        Assert.AreEqual(h1.Meta.BatchId, queue.DequeueAndSetRunning(runners)!.Meta.BatchId);
        Assert.IsNull(queue.DequeueAndSetRunning(runners));
    }

    /// <summary>The sqlite store does the same, and passes over batches of a type the database has no
    /// runner for - which would otherwise stop the whole queue from the head of the line.</summary>
    [TestMethod]
    public void TheSqliteStorePassesOverWhatCannotStart() {
        var probe = new Probe();
        var low = new ConcurrencyProbeRunner(probe);
        var high = new HighPriorityProbeRunner();
        var all = new Dictionary<string, ITaskRunner> { [low.TaskTypeId] = low, [high.TaskTypeId] = high };
        var path = Path.Combine(Path.GetTempPath(), "relatude-queue-test-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try {
            using (var queue = new SqliteQueueStore(path)) {
                var t0 = DateTime.UtcNow.AddMinutes(-10);
                var l1 = batch(low, new ConcurrencyProbeTask(1), t0);
                var l2 = batch(low, new ConcurrencyProbeTask(2), t0.AddMinutes(1));
                var h1 = batch(high, new HighPriorityProbeTask(3), t0.AddMinutes(2));
                foreach (var b in new[] { l1, l2, h1 }) queue.Enqueue(b, all[b.Meta.TaskTypeId]);

                // no runner for the high priority type: it is left where it is
                var onlyLow = new Dictionary<string, ITaskRunner> { [low.TaskTypeId] = low };
                var first = queue.DequeueAndSetRunning(onlyLow)!;
                Assert.AreEqual(l1.Meta.BatchId, first.Meta.BatchId);
                Assert.AreEqual(BatchState.Running, first.Meta.State);
                Assert.AreEqual(1, first.TaskCount);
                // turned down: the next in line
                var none = new HashSet<string>();
                Assert.IsNull(queue.DequeueAndSetRunning(all, none, b => b.Meta.BatchId == l1.Meta.BatchId));
                Assert.AreEqual(l2.Meta.BatchId, queue.DequeueAndSetRunning(all, none, b => b.Meta.BatchId != h1.Meta.BatchId)!.Meta.BatchId);
                // a full type is passed over in the query itself
                Assert.IsNull(queue.DequeueAndSetRunning(all, new HashSet<string> { high.TaskTypeId }, null));
                Assert.AreEqual(h1.Meta.BatchId, queue.DequeueAndSetRunning(all)!.Meta.BatchId);
                Assert.AreEqual(3, queue.CountBatch(BatchState.Running));
                Assert.AreEqual(0, queue.CountBatch(BatchState.Pending));
            }
        } finally {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) {
                try { File.Delete(file); } catch { }
            }
        }
    }

    [TestMethod]
    public void AnEntryNamesItsTypeByFullNameOrClassName() {
        var settings = new SettingsLocal {
            TaskConcurrency = [
                new() { TaskType = typeof(SemanticIndexTask).FullName, MaxConcurrency = 8 },
                new() { TaskType = " textindextask ", MaxConcurrency = 2 },
                new() { TaskType = nameof(TextIndexTask), MaxConcurrency = 5 }, // the first entry naming a type counts
                new() { TaskType = nameof(RewriteTask) }, // no number: left to the runner
                new() { MaxConcurrency = 3 }, // no type: names nothing
            ],
        };
        Assert.AreEqual(8, settings.FindTaskConcurrency(typeof(SemanticIndexTask).FullName!));
        Assert.AreEqual(2, settings.FindTaskConcurrency(typeof(TextIndexTask).FullName!));
        Assert.IsNull(settings.FindTaskConcurrency(typeof(RewriteTask).FullName!));
        Assert.IsNull(settings.FindTaskConcurrency("Some.Other.Task"));
        Assert.IsNull(new SettingsLocal().FindTaskConcurrency(typeof(TextIndexTask).FullName!));
    }

    // ---- pieces ----

    static DataStoreLocal open(ITaskRunner runner, SettingsLocal? settings = null) {
        settings ??= new();
        settings.AutoDequeTasks = false; // the tests drive the queue themselves
        var store = new DataStoreLocal(Helper.GetDatamodel(), settings);
        store.RegisterRunner(runner);
        store.Open();
        return store;
    }

    // one batch per task: the probe runner takes one task per batch
    static void enqueue(DataStoreLocal store, Probe probe, params int[] keys) {
        foreach (var key in keys) {
            probe.Gate(key);
            store.EnqueueTask(new ConcurrencyProbeTask(key));
        }
    }
    static void enqueue(DataStoreLocal store, Probe probe, string jobId, int key) {
        probe.Gate(key);
        store.EnqueueTask(new ConcurrencyProbeTask(key), jobId);
    }

    static IBatch batch(ITaskRunner runner, TaskData task, DateTime created) {
        var b = runner.CreateBatchWithOneTask(task, BatchState.Pending, null);
        b.Meta.CreatedUtc = created;
        return b;
    }

    static void waitUntil(Func<bool> condition, string what, int timeoutMs = 10_000) {
        var sw = Stopwatch.StartNew();
        while (!condition()) {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail("Timed out waiting for " + what + ".");
            Thread.Sleep(5);
        }
    }

    // time for the queue to do what it must not: start something it should hold back
    static void settle() => Thread.Sleep(150);
}

/// <summary>What the probe runners saw: how many batches ran at once, overall and per key, and how
/// often each key ran. Every run waits at its key's gate until the test opens it.</summary>
sealed class Probe {
    readonly object _lock = new();
    readonly Dictionary<int, int> _activeByKey = [];
    readonly Dictionary<int, int> _runsByKey = [];
    readonly ConcurrentDictionary<int, SemaphoreSlim> _gates = new();
    volatile bool _gated = true;
    public int Current { get { lock (_lock) return _current; } }
    public int Max { get { lock (_lock) return _max; } }
    public int MaxPerKey { get { lock (_lock) return _maxPerKey; } }
    int _current, _max, _maxPerKey;
    public SemaphoreSlim Gate(int key) => _gates.GetOrAdd(key, _ => new SemaphoreSlim(0));
    public void Release(int key) => Gate(key).Release();
    /// <summary>Opens every gate, now and for every run still to come.</summary>
    public void ReleaseAll() {
        _gated = false;
        foreach (var gate in _gates.Values) gate.Release(1000);
    }
    public async Task WaitAtGate(int key) {
        if (_gated && !await Gate(key).WaitAsync(TimeSpan.FromSeconds(20))) throw new TimeoutException("Gate " + key + " was never opened.");
    }
    public void Enter(int key) {
        lock (_lock) {
            _current++;
            _max = Math.Max(_max, _current);
            _activeByKey[key] = _activeByKey.GetValueOrDefault(key) + 1;
            _maxPerKey = Math.Max(_maxPerKey, _activeByKey[key]);
            _runsByKey[key] = _runsByKey.GetValueOrDefault(key) + 1;
        }
    }
    public void Exit(int key) {
        lock (_lock) {
            _current--;
            _activeByKey[key]--;
        }
    }
    public int Runs(int key) { lock (_lock) return _runsByKey.GetValueOrDefault(key); }
    public int Active(int key) { lock (_lock) return _activeByKey.GetValueOrDefault(key); }
    public int[] ActiveKeys() { lock (_lock) return [.. _activeByKey.Where(kv => kv.Value > 0).Select(kv => kv.Key)]; }
}

sealed class ConcurrencyProbeTask(int key) : TaskData {
    public int Key { get; } = key;
}

sealed class ConcurrencyProbeRunner(Probe probe) : TaskRunner<ConcurrencyProbeTask> {
    public int Concurrency { get; set; } = 1;
    public int Limit { get; set; } = TaskQueue.MaxSupportedConcurrency;
    public bool Keyed { get; set; }
    public bool Fail { get; set; }
    public override BatchTaskPriority Priority => BatchTaskPriority.Low;
    public override int MaxTaskCountPerBatch => 1;
    public override bool PersistToDisk => false;
    public override bool DeleteOnSuccess => true;
    public override int MaxConcurrency => Concurrency;
    public override int MaxConcurrencyLimit => Limit;
    public override string? GetConcurrencyKey(ConcurrencyProbeTask task) => Keyed ? task.Key.ToString() : null;
    public override TimeSpan GetMaximumAgeInQueueAfterExecution() => TimeSpan.FromHours(1);
    public override async Task ExecuteAsync(Batch<ConcurrencyProbeTask> batch, TaskLogger? taskLogger) {
        var key = batch.Tasks[0].Key;
        probe.Enter(key);
        try {
            await probe.WaitAtGate(key);
            if (Fail) throw new Exception("The probe was told to fail.");
        } finally {
            probe.Exit(key);
        }
    }
    public override byte[] TaskToBytes(ConcurrencyProbeTask task) => BitConverter.GetBytes(task.Key);
    public override ConcurrencyProbeTask TaskFromBytes(byte[] bytes) => new(BitConverter.ToInt32(bytes, 0));
}

sealed class HighPriorityProbeTask(int key) : TaskData {
    public int Key { get; } = key;
}

sealed class HighPriorityProbeRunner : TaskRunner<HighPriorityProbeTask> {
    public override BatchTaskPriority Priority => BatchTaskPriority.High;
    public override int MaxTaskCountPerBatch => 1;
    public override bool PersistToDisk => false;
    public override bool DeleteOnSuccess => true;
    public override TimeSpan GetMaximumAgeInQueueAfterExecution() => TimeSpan.FromHours(1);
    public override Task ExecuteAsync(Batch<HighPriorityProbeTask> batch, TaskLogger? taskLogger) => Task.CompletedTask;
    public override byte[] TaskToBytes(HighPriorityProbeTask task) => BitConverter.GetBytes(task.Key);
    public override HighPriorityProbeTask TaskFromBytes(byte[] bytes) => new(BitConverter.ToInt32(bytes, 0));
}
