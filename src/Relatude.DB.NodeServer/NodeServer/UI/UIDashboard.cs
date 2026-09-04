using System.Diagnostics;
using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.IO;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The landing page of one database: what it holds, what it is doing right now, and what is worth
/// noticing before anything else.
///
/// Split in two on purpose, because the two halves cost very different amounts:
///   - "dashboard" is the full picture and calls <see cref="IDataStore.GetInfoAsync"/>, which takes
///     the store's write lock and counts every type, every backup file and every state file. Worth
///     it once when the page opens, never worth it on a timer;
///   - "dashboard-live" is what the page asks for every couple of seconds. It reads counters that
///     are already kept (see <see cref="StoreCounters"/>), the activity list and the queue depths,
///     and touches no file at all. The rate graph is built in the browser from the difference
///     between two of these, which is why the counters have to be cumulative and cheap rather than
///     pre-averaged.
/// </summary>
sealed class UIDashboard {
    readonly RelatudeDBServer _server;
    internal UIDashboard(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("dashboard", async ctx => await full(ctx.Payload<StorePayload>().StoreId));
        commands.Register("dashboard-live", ctx => live(ctx.Payload<StorePayload>().StoreId));
        commands.Register("dashboard-clear-cache", async ctx => await clearCache(ctx.Payload<StorePayload>().StoreId));
    }

    NodeStoreContainer container(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        return c;
    }

    // ---- the whole picture, once ----

    async Task<object> full(Guid storeId) {
        var c = container(storeId);
        var settings = c.Settings;
        var state = c.HasFailed ? "Error" : c.Store?.State.ToString() ?? "Closed";
        if (c.Store == null || c.Store.State != DataStoreState.Open) {
            return new {
                Open = false,
                State = state,
                Name = string.IsNullOrEmpty(settings.Name) ? settings.Id.ToString() : settings.Name,
                StoreId = settings.Id,
                StartupError = startupError(c),
                // a closed database still has files, and their size is the one thing worth showing
                // before anyone decides whether to open it
                Files = closedFileSizes(settings),
                Engines = engines(settings),
            };
        }
        var store = c.Store;
        var info = await store.Datastore.GetInfoAsync();
        var datamodel = store.Datastore.Datamodel;
        var types = buildTypes(datamodel, info.TypeCounts);
        return new {
            Open = true,
            State = state,
            Name = string.IsNullOrEmpty(settings.Name) ? settings.Id.ToString() : settings.Name,
            StoreId = settings.Id,
            StartupError = startupError(c),
            // the numbers that do not move on their own: the shape of the database
            UptimeMs = info.UptimeMs,
            StartUpMs = info.StartUpMs,
            OpenedUtc = utc(info.InitiatedUtc),
            FirstChangeUtc = utc(info.LogFirstStateUtc),
            LastChangeUtc = utc(info.LogLastChange),
            Files = new {
                Database = info.LogFileSize,
                State = info.LogStateFileSize,
                Logs = info.LoggingFileSize,
                Backups = info.BackupFileSize,
                Secondary = info.SecondaryLogFileSize,
            },
            Datamodel = new {
                NodeTypes = info.DatamodelNodeTypeCount,
                Properties = info.DatamodelPropertyCount,
                Relations = info.DatamodelRelationCount,
                Indexes = info.DatamodelIndexCount,
            },
            Types = types,
            Sources = UIModelInfo.Sources(datamodel),
            // what maintenance would find: the page says it, the Storage section does it
            Maintenance = new {
                ActionsNotInState = info.LogActionsNotItInStatefile,
                TruncatableActions = info.LogTruncatableActions,
                IndexesOutOfSync = info.NoIndexesOutOfSync,
                RunningRewrite = info.RunningRewriteFile,
            },
            Cache = new {
                NodeCacheSizePercentage = info.NodeCacheSizePercentage,
                info.NodeCacheHits,
                info.NodeCacheMisses,
                info.NodeCacheOverflows,
                SetCacheSizePercentage = info.SetCacheSizePercentage,
                info.SetCacheHits,
                info.SetCacheMisses,
                info.SetCacheOverflows,
                info.AggregateCacheCount,
                info.AggregateCacheHits,
                info.AggregateCacheMisses,
            },
            Engines = engines(settings),
            Ai = settings.AISettings == null ? null : new {
                Provider = string.IsNullOrEmpty(settings.AISettings.Name) ? settings.AISettings.TypeName : settings.AISettings.Name,
                settings.AISettings.EmbeddingModel,
            },
            RelationTypes = datamodel.Relations.Count,
        };
    }

    // ---- throwing the warm caches away ----

    /// <summary>
    /// Empties the node, result set and index caches of one database, which is what the page offers
    /// when a measurement should start from cold - and the only way to see what the caches are
    /// actually holding, since the store frees and compacts on its way out of
    /// <see cref="MaintenanceAction.ClearCache"/>. Two things follow that the page has to expect:
    /// the counters the activity graph is built from start over (drawn as a gap, never a negative
    /// rate), and the indexes warm again in the background, so the numbers keep moving afterwards.
    /// </summary>
    async Task<object> clearCache(Guid storeId) {
        var c = container(storeId);
        var store = c.Store ?? throw new Exception("The database must be open. ");
        if (store.State != DataStoreState.Open) throw new Exception("The database must be open. ");
        // read before, not after: after the clear there is nothing left to count
        var counters = store.Datastore.PeekCounters();
        var watch = Stopwatch.StartNew();
        var before = GC.GetTotalMemory(false);
        await store.MaintenanceAsync(MaintenanceAction.ClearCache);
        var after = GC.GetTotalMemory(false);
        watch.Stop();
        return new {
            EntriesCleared = (long)counters.NodeCacheCount + counters.SetCacheCount,
            // another thread allocating during the clear can leave this negative, which reads as
            // nonsense; nothing freed is the honest floor
            FreedBytes = Math.Max(0, before - after),
            ManagedBytes = after,
            ElapsedMs = watch.ElapsedMilliseconds,
        };
    }

    // ---- the moving parts, every couple of seconds ----

    object live(Guid storeId) {
        var c = container(storeId);
        var state = c.HasFailed ? "Error" : c.Store?.State.ToString() ?? "Closed";
        if (c.Store == null || c.Store.State != DataStoreState.Open) {
            // a database that is opening is the one worth watching most closely: it reports how far
            // the log replay has come, and its activities say what it is replaying
            object? opening = null;
            object[] busy = [];
            if (c.Store != null && c.Store.State == DataStoreState.Opening) {
                try {
                    var progress = c.Store.Datastore.GetOpeningStatus();
                    opening = new { progress.ProgressPercentage, progress.TimeRemainingMs, progress.TimeElapsedMs };
                    busy = activities(c.Store.Datastore.GetStatus());
                } catch { } // a store that finishes opening mid-call has nothing to report, not an error
            }
            return new {
                Open = false,
                State = state,
                SampledUtc = utc(DateTime.UtcNow),
                // the heap belongs to the server process, not to this database: worth reporting
                // even while it is closed, since the memory it used is only released gradually
                ManagedMemory = GC.GetTotalMemory(false),
                ProcessMemory = workingSet(),
                Opening = opening,
                Activities = busy,
            };
        }
        var datastore = c.Store.Datastore;
        var counters = datastore.PeekCounters();
        var status = datastore.GetStatus();
        var conversions = datastore.GetConversions();
        return new {
            Open = true,
            State = state,
            // the client turns the counters into rates, so it needs to know exactly when they were
            // read - not when the response happened to arrive
            SampledUtc = utc(DateTime.UtcNow),
            // memory is the server process, not this database - the graph says so, and the
            // "collect garbage" button next to it acts on the same process
            ManagedMemory = GC.GetTotalMemory(false),
            ProcessMemory = workingSet(),
            counters.NodeCount,
            counters.RelationCount,
            counters.Queries,
            counters.Transactions,
            counters.Actions,
            counters.NodeReads,
            counters.NodeCacheCount,
            counters.NodeCacheSize,
            counters.SetCacheCount,
            counters.SetCacheSize,
            TasksQueued = counters.TasksQueued + counters.TasksQueuedPersisted,
            Conversions = new { conversions.Running, conversions.Queued, conversions.Failed },
            Activities = activities(status),
        };
    }

    // ---- pieces both halves use ----

    /// <summary>
    /// What the operating system has the process resident in memory, or 0 where it cannot be read.
    /// Sampled on the live cadence, so it must not throw and must not be expensive: this is a single
    /// counter read, unlike <see cref="GC.GetTotalMemory"/> with collection forced.
    /// </summary>
    static long workingSet() {
        try {
            return Environment.WorkingSet;
        } catch {
            return 0;
        }
    }

    // flattened: the tree is one level deep in practice, and a nested list of one-liners reads
    // worse than the list itself
    static object[] activities(DataStoreStatus status) {
        return [.. status.ActivityTree
            .SelectMany(branch => new[] { branch.Activity }.Concat(branch.Children.Select(child => child.Activity)))
            .Select(a => (object)new { Category = a.Category.ToString(), a.Description, a.PercentageProgress })];
    }

    static object? startupError(NodeStoreContainer c) {
        if (c.StartUpException == null) return null;
        return new { TimeUtc = utc(c.StartUpExceptionDateTimeUTC), c.StartUpException.Message };
    }

    /// <summary>The engines actually behind this database, named as one line each: which ones are in
    /// use is a setting, but it is the first thing to check when something behaves unexpectedly.</summary>
    static object engines(Settings.NodeStoreContainerSettings settings) {
        var local = settings.LocalSettings;
        return new {
            TextIndex = local == null ? "-" : describe(local.DefaultTextEngine),
            ValueIndex = local == null ? "-" : describe(local.DefaultValueEngine),
            Queue = local?.PersistedQueueStoreEngine.ToString() ?? "-",
            // semantic indexes only exist with an AI provider; null reads as "off" in the UI
            SemanticIndex = settings.AISettings == null || local == null ? null : describe(local.DefaultVectorEngine),
        };
        // the default engine of a kind, or the memory index when the default names none
        static string describe(IndexEngineSettings? engine) => engine == null ? "Memory" : engine.ToString();
    }

    /// <summary>What the files take while the database is closed, read straight off the providers.</summary>
    object closedFileSizes(Settings.NodeStoreContainerSettings settings) {
        long database = 0, state = 0, backups = 0;
        try {
            if (settings.IoDatabase is Guid dbIoId && dbIoId != Guid.Empty) {
                var io = _server.GetIO(dbIoId);
                foreach (var key in FileKeyUtility.WAL_GetAllFileKeys(io)) database += io.GetFileSizeOrZeroIfUnknown(key);
            }
            var indexIoId = settings.IoIndexes is Guid indexes && indexes != Guid.Empty ? indexes : settings.IoDatabase;
            if (indexIoId is Guid stateIoId && stateIoId != Guid.Empty) {
                var io = _server.GetIO(stateIoId);
                foreach (var key in FileKeyUtility.State_GetAllFileKeys(io)) state += io.GetFileSizeOrZeroIfUnknown(key);
            }
            var backupIoId = settings.IoBackup is Guid backup && backup != Guid.Empty ? backup : settings.IoDatabase;
            if (backupIoId is Guid id && id != Guid.Empty) {
                var io = _server.GetIO(id);
                foreach (var key in FileKeyUtility.WAL_GetAllBackUpFileKeys(io)) backups += io.GetFileSizeOrZeroIfUnknown(key);
            }
        } catch { } // a provider that cannot be reached must not stop the page from rendering
        return new { Database = database, State = state, Logs = 0L, Backups = backups, Secondary = 0L };
    }

    // the full name carries the namespace, which is the same for every type of one datamodel and
    // only crowds the column; the full name is still sent, for the title
    /// <summary>
    /// What the database holds, per node type, both ways of counting it: <c>Count</c> is the nodes
    /// whose own type this is, <c>CountAll</c> those plus every node of a type below it. The two
    /// answer different questions - which types the data actually is, and how much lives under an
    /// abstraction - and the difference is the whole point of the panel's "include inherited" switch:
    /// an interface or an abstract base has no nodes of its own and would otherwise never appear,
    /// while with inheritance counted in a parent and its children each count the same nodes, so the
    /// two must never be mixed in one picture.
    ///
    /// The counts come from the store's per-type tallies (a lookup, not a scan); the sums are walked
    /// over the model's precomputed descendant sets. The whole list is sent - a few dozen entries at
    /// most - and how many of them to show is the page's decision, not this one's.
    /// </summary>
    static object[] buildTypes(Datamodels.Datamodel datamodel, Dictionary<string, int> counts) {
        var rows = new List<(Datamodels.NodeTypeModel Type, int Own, long All)>();
        foreach (var t in datamodel.NodeTypes.Values) {
            if (t.Id == Datamodels.NodeConstants.BaseNodeTypeId) continue; // every node at once: a total, not a type
            if (t.IsInnerNode) continue; // lives inside another node's property and is not counted on its own
            counts.TryGetValue(t.FullName, out var own);
            long all = 0;
            foreach (var d in t.ThisAndDescendingTypes.Values) {
                if (counts.TryGetValue(d.FullName, out var c)) all += c;
            }
            if (own == 0 && all == 0) continue; // a type with nothing in it either way
            rows.Add((t, own, all));
        }
        return [.. rows
            .OrderByDescending(r => r.All)
            .ThenByDescending(r => r.Own)
            .ThenBy(r => r.Type.CodeName, StringComparer.OrdinalIgnoreCase)
            .Select(r => (object)new {
                r.Type.Id,
                Name = r.Type.CodeName,
                Full = r.Type.FullName,
                Count = r.Own,
                CountAll = r.All,
                Kind = r.Type.ModelType.ToString(),
                r.Type.IsInterface,
                SourceId = r.Type.DatamodelSourceId,
                // who this type is under: with inheritance counted in, a parent and its children
                // count the same nodes, so a picture of shares has to drop whatever already sits
                // inside something else it is showing
                Parents = r.Type.Parents.Where(p => p != Datamodels.NodeConstants.BaseNodeTypeId).ToArray(),
            })];
    }

    static string? utc(DateTime? value) {
        if (value is not DateTime v) return null;
        return DateTime.SpecifyKind(v, DateTimeKind.Utc).ToString("o");
    }

    sealed record StorePayload(Guid StoreId);
}
