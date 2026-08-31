using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.Tasks;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The background work of one database: text indexing, semantic indexing and log rewrites, all of
/// which happen off the transaction that caused them.
///
/// A database has two queues and they are not interchangeable, so the page shows both rather than a
/// single total: <see cref="IDataStore.TaskQueue"/> holds the task types that are cheap to lose and
/// keeps them in memory only, while <see cref="IDataStore.TaskQueuePersisted"/> holds the ones that
/// have to survive a restart (its own engine - native file or sqlite - is a setting). Which queue a
/// task lands in is decided by its runner's PersistToDisk, never by the caller, which is why the type
/// list says where each type goes.
///
/// The controls are the ones an operator actually needs when something has gone wrong: put a failed
/// batch back in line, cancel one that should not run, delete what is finished with, and turn the
/// whole queue's share of the machine up or down while a backlog drains.
/// </summary>
sealed class UITasks {
    const int maxPageSize = 500;
    // Only these two are safe to set from outside: anything else would either stand for work that
    // never happened (Completed) or hand a batch to a runner that is not holding it (Running).
    static readonly BatchState[] settableStates = [BatchState.Pending, BatchState.Cancelled];

    readonly RelatudeDBServer _server;
    internal UITasks(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("tasks", ctx => tasks(ctx.Payload<TasksPayload>()));
        commands.Register("tasks-set-state", ctx => setState(ctx.Payload<TasksStatePayload>()));
        commands.Register("tasks-delete", ctx => delete(ctx.Payload<TasksDeletePayload>()));
        commands.Register("tasks-clear", ctx => clear(ctx.Payload<TasksClearPayload>()));
        commands.Register("tasks-throttle", ctx => throttle(ctx.Payload<TasksThrottlePayload>()));
    }

    // ---- the page ----

    object tasks(TasksPayload p) {
        var c = container(p.StoreId);
        var state = c.HasFailed ? "Error" : c.Store?.State.ToString() ?? "Closed";
        if (!c.IsOpen()) {
            // the queues live in the open store: a closed database has a queue file on disk, but
            // nothing that can read it, and saying so beats an empty table that looks like "no work"
            return new { Open = false, State = state, Queues = Array.Empty<object>(), Types = Array.Empty<object>(), Batches = Array.Empty<object>(), Total = 0 };
        }
        var store = c.Store!.Datastore;
        var selectedId = p.Queue == persistedId && store.TaskQueuePersisted != null ? persistedId : memoryId;
        var selected = selectedId == persistedId ? store.TaskQueuePersisted! : store.TaskQueue;
        var states = parseStates(p.States);
        var typeIds = p.TypeIds ?? [];
        var pageSize = Math.Clamp(p.PageSize, 1, maxPageSize);
        var page = Math.Max(0, p.Page);
        var batches = selected.GetBatchMeta(states, typeIds, [], page, pageSize, out var total);
        // a page past the end of a queue that drained while it was being looked at reads as empty,
        // which is indistinguishable from "nothing here": step back to the last page that exists
        if (batches.Length == 0 && total > 0 && page > 0) {
            page = Math.Max(0, (total - 1) / pageSize);
            batches = selected.GetBatchMeta(states, typeIds, [], page, pageSize, out total);
        }
        return new {
            Open = true,
            State = state,
            Throttle = store.TaskQueueThrottle,
            Queues = new[] {
                queueInfo(memoryId, "In memory", store.TaskQueue, null),
                // named by the setting rather than by the class behind it: "Native" is what the
                // settings page calls it, and the class is the same one for two of the three choices
                store.TaskQueuePersisted == null ? null : queueInfo(persistedId, "Persisted", store.TaskQueuePersisted, c.Settings.LocalSettings?.PersistedQueueStoreEngine.ToString() ?? "-"),
            }.Where(q => q != null),
            Types = store.TaskQueue.Runners
                .OrderBy(r => r.TaskTypeId)
                .Select(r => new {
                    Id = r.TaskTypeId,
                    Name = typeName(r.TaskTypeId),
                    Priority = r.Priority.ToString(),
                    Queue = r.PersistToDisk ? persistedId : memoryId,
                    MaxTasksPerBatch = r.MaxTaskCountPerBatch,
                    // both explain why finished batches are not in the list: most types are deleted
                    // the moment they succeed, and the rest are swept once they are old enough
                    r.DeleteOnSuccess,
                    RetentionMs = finiteMs(r.GetMaximumAgeInQueuePerState(BatchState.Completed)),
                    RestartOnStartup = r.RestartTaskBatchesOnStartupThatStartedButNeverFailedOrCompleted,
                }),
            Queue = selectedId,
            Batches = batches.Select(b => new {
                b.BatchId,
                TypeId = b.TaskTypeId,
                Type = typeName(b.TaskTypeId),
                State = b.State.ToString(),
                Priority = b.Priority.ToString(),
                b.TaskCount,
                CreatedUtc = utc(b.CreatedUtc),
                CompletedUtc = utc(b.Completed),
                b.JobId,
                b.ErrorType,
                b.ErrorMessage,
            }),
            Total = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    object queueInfo(string id, string label, TaskQueue queue, string? engine) {
        var batchCounts = queue.BatchCountsPerState().ToDictionary(kv => kv.Key, kv => kv.Value);
        var taskCounts = queue.TaskCountsPerState().ToDictionary(kv => kv.Key, kv => kv.Value);
        return new {
            Id = id,
            Label = label,
            // the persisted queue's engine is a setting and the first thing to check when a restart
            // did not keep what it should have; the memory queue has no choice to report
            Engine = engine,
            Persisted = engine != null,
            Counts = batchCounts.Keys.Union(taskCounts.Keys)
                .OrderBy(s => (int)s)
                .Select(s => new {
                    State = s.ToString(),
                    Batches = batchCounts.TryGetValue(s, out var b) ? b : 0,
                    Tasks = taskCounts.TryGetValue(s, out var t) ? t : 0,
                }),
            // only ever an estimate, and only once the queue has been running long enough to have a
            // rate to extrapolate from - null the rest of the time rather than a made-up number
            EstimatedEmptyMs = finiteMs(queue.EstimateDurationUntilEmpty()),
        };
    }

    // ---- the controls ----

    object setState(TasksStatePayload p) {
        var queue = selectQueue(p.StoreId, p.Queue);
        var state = parseState(p.State);
        if (!settableStates.Contains(state)) throw new Exception("A batch can only be set to " + string.Join(" or ", settableStates) + ". ");
        if (p.BatchIds.Length == 0) return new { Changed = 0 };
        queue.SetState(p.BatchIds, state);
        return new { Changed = p.BatchIds.Length };
    }

    object delete(TasksDeletePayload p) {
        var queue = selectQueue(p.StoreId, p.Queue);
        if (p.BatchIds.Length == 0) return new { Deleted = 0 };
        queue.DeleteById(p.BatchIds);
        return new { Deleted = p.BatchIds.Length };
    }

    /// <summary>
    /// Deletes by state and type rather than by id, so "clear everything that failed" stays one call
    /// however long the list is. Both filters empty deletes the whole queue - the caller says so by
    /// sending nothing, and the page asks before it does.
    /// </summary>
    object clear(TasksClearPayload p) {
        var queue = selectQueue(p.StoreId, p.Queue);
        queue.DeleteByStateOrType(parseStates(p.States), p.TypeIds ?? []);
        return new { Done = true };
    }

    object throttle(TasksThrottlePayload p) {
        var store = openStore(p.StoreId);
        store.TaskQueueThrottle = Math.Clamp(p.Throttle, 0, 100);
        // the scheduler clamps too, and reports back what it actually took
        return new { Throttle = store.TaskQueueThrottle };
    }

    // ---- pieces ----

    const string memoryId = "memory";
    const string persistedId = "persisted";

    NodeStoreContainer container(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        return c;
    }

    IDataStore openStore(Guid storeId) {
        var c = container(storeId);
        if (!c.IsOpen()) throw new Exception("The database must be open. ");
        return c.Store!.Datastore;
    }

    TaskQueue selectQueue(Guid storeId, string? queueId) {
        var store = openStore(storeId);
        if (queueId != persistedId) return store.TaskQueue;
        return store.TaskQueuePersisted ?? throw new Exception("This database has no persisted queue. ");
    }

    static BatchState[] parseStates(string[]? values) {
        if (values == null || values.Length == 0) return [];
        return [.. values.Select(parseState)];
    }

    static BatchState parseState(string value) {
        return Enum.TryParse<BatchState>(value, true, out var state) ? state : throw new Exception("Unknown task state: " + value + ". ");
    }

    /// <summary>The type name without its namespace, spaced out: "TextIndexTask" reads as "Text index task".</summary>
    static string typeName(string typeId) {
        var index = typeId.LastIndexOf('.');
        var name = index > -1 && index < typeId.Length - 1 ? typeId[(index + 1)..] : typeId;
        return name.Decamelize();
    }

    /// <summary>TimeSpan.MaxValue is how a runner says "kept forever"; it is not a number to show.</summary>
    static double? finiteMs(TimeSpan? value) {
        if (value is not TimeSpan v || v == TimeSpan.MaxValue || v == Timeout.InfiniteTimeSpan) return null;
        return v.TotalMilliseconds;
    }

    static string? utc(DateTime? value) {
        if (value is not DateTime v) return null;
        return DateTime.SpecifyKind(v, DateTimeKind.Utc).ToString("o");
    }

    sealed record TasksPayload(Guid StoreId, string? Queue, string[]? States, string[]? TypeIds, int Page = 0, int PageSize = 50);
    sealed record TasksStatePayload(Guid StoreId, string? Queue, Guid[] BatchIds, string State);
    sealed record TasksDeletePayload(Guid StoreId, string? Queue, Guid[] BatchIds);
    sealed record TasksClearPayload(Guid StoreId, string? Queue, string[]? States, string[]? TypeIds);
    sealed record TasksThrottlePayload(Guid StoreId, int Throttle);
}
