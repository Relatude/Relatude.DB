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
    // a long tail of node types is a scroll, not a summary; the rest is counted into one line
    const int maxTypes = 12;

    readonly RelatudeDBServer _server;
    internal UIDashboard(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("dashboard", async ctx => await full(ctx.Payload<StorePayload>().StoreId));
        commands.Register("dashboard-live", ctx => live(ctx.Payload<StorePayload>().StoreId));
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
        var types = info.TypeCounts
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ToArray();
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
            Types = types.Take(maxTypes).Select(kv => new { Name = shortTypeName(kv.Key), Full = kv.Key, Count = kv.Value }),
            OtherTypes = types.Length > maxTypes ? types.Length - maxTypes : 0,
            OtherTypeNodes = types.Skip(maxTypes).Sum(kv => (long)kv.Value),
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
            return new { Open = false, State = state, SampledUtc = utc(DateTime.UtcNow), Opening = opening, Activities = busy };
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
            TextIndex = local?.PersistedTextIndexEngine.ToString() ?? "-",
            ValueIndex = local?.PersistedValueIndexEngine.ToString() ?? "-",
            Queue = local?.PersistedQueueStoreEngine.ToString() ?? "-",
            SemanticIndex = settings.AISettings?.IndexType.ToString(),
        };
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
    static string shortTypeName(string fullName) {
        var index = fullName.LastIndexOf('.');
        return index > -1 && index < fullName.Length - 1 ? fullName[(index + 1)..] : fullName;
    }

    static string? utc(DateTime? value) {
        if (value is not DateTime v) return null;
        return DateTime.SpecifyKind(v, DateTimeKind.Utc).ToString("o");
    }

    sealed record StorePayload(Guid StoreId);
}
