using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.NodeServer.API;
using Relatude.DB.Tasks;
using System.Diagnostics;
namespace Relatude.DB.NodeServer.UI;
/// <summary>
/// The long jobs the admin UI starts - a truncation, a backup, demo content, a move of files - kept
/// on the server where every session can see them:
/// <code>
///   shared-tasks         every task running now, and the ones that finished in the last minute
///   shared-task-report   the browser that runs a task says where it is (and that it is still there)
///   shared-task-cancel   stops one, from whichever session asks
///   shared-task-job      the server side of one task alone: what the tab that started it waits for
/// </code>
/// A task has up to two sides. The browser that started it reports what its progress dialog shows,
/// every few seconds even when nothing moved, which is what tells the board it is still there. The
/// server side is the job itself, where there is one: a command that starts work that outlives the
/// request attaches it under the id the browser gave the task (see <see cref="Attach"/>,
/// <see cref="RunAsync{T}"/>), and from then on the job is what the task says. That is what lets a
/// task survive the page that started it: reload the tab, or open another one, and the truncation is
/// still on the board, counting, because the server is the one counting. A task with no server side -
/// an upload, a download, which run in the browser - ends with its tab, and says so.
/// <para>The ids come from the browser, so the dialog that started a task and the board agree on
/// which task it is without a round trip first. Thread-safe.</para>
/// </summary>
public sealed class UISharedTasks {
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "error";
    public const string Cancelled = "cancelled";
    // a finished task stays listed this long, so a session that looks a little later still sees how it went
    const int keepFinishedMs = 60_000;
    // A browser reports at least every five seconds, and says so when its tab goes away; this much
    // silence means it went without saying. Generous, because a tab in the background is given a
    // timer once a minute at worst - while the transfer it runs carries on.
    const int browserSilenceMs = 75_000;
    // a job is asked where it is at most this often, however many tabs are following the board
    const int pollEveryMs = 500;
    readonly RelatudeDBServer _server;
    readonly Dictionary<Guid, SharedTask> _tasks = [];
    readonly Stopwatch _clock = Stopwatch.StartNew();
    internal UISharedTasks(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("shared-tasks", ctx => List());
        commands.Register("shared-task-report", ctx => report(ctx.Payload<SharedTaskReport>(), ctx.Http));
        commands.Register("shared-task-cancel", ctx => cancel(ctx.Payload<SharedTaskIdPayload>().Id));
        commands.Register("shared-task-job", ctx => JobOf(ctx.Payload<SharedTaskIdPayload>().Id));
    }

    /// <summary>
    /// What the job behind a task says, leaving out what its browser reports - the browser asking is
    /// the one waiting for it, and its own "still running" would never let it stop. Null when nothing
    /// was attached (yet).
    /// </summary>
    public SharedTaskProgress? JobOf(Guid id) {
        lock (_tasks) {
            if (!_tasks.TryGetValue(id, out var t) || t.Poll == null) return null;
            pollIfDue(t, _clock.ElapsedMilliseconds);
            return t.Server;
        }
    }

    /// <summary>
    /// The task a command was asked to run as, read off its payload's <c>taskId</c>: null when the
    /// caller does not share its progress (the old admin UI, code), and then nothing is attached.
    /// </summary>
    public SharedTaskRef? RefOf(UICommandContext ctx, Guid? storeId, string title) {
        var id = ctx.Payload<SharedTaskIdPayload>().TaskId;
        if (id is not Guid taskId || taskId == Guid.Empty) return null;
        return new SharedTaskRef(taskId, storeId, title, userOf(ctx.Http));
    }

    /// <summary>
    /// Makes a job the server side of the task: <paramref name="poll"/> says where it is whenever the
    /// board is listed (at most every half second), and <paramref name="cancel"/>, when there is one,
    /// is what Cancel does from any session. A task can be attached again - the second of two scans
    /// shown in one dialog - and the new job takes over.
    /// </summary>
    public void Attach(SharedTaskRef? task, Func<SharedTaskProgress> poll, Action? cancel = null) {
        if (task is not SharedTaskRef t) return;
        lock (_tasks) {
            var entry = getOrAdd(t.Id, t.StoreId, t.Title, t.StartedBy);
            entry.Poll = poll;
            entry.Cancel = cancel;
            entry.Server = null;
            entry.PolledMs = -pollEveryMs; // due at once
            entry.FinishedUtc = null;
        }
    }

    /// <summary>A background job of the kind the file scans and the demo generators run as.</summary>
    internal void Attach(SharedTaskRef? task, FileScanJob job) {
        Attach(task, () => new SharedTaskProgress(
            job.State switch {
                FileScanJob.Done => Done,
                FileScanJob.Cancelled => Cancelled,
                FileScanJob.Failed => Failed,
                _ => Running,
            },
            job.Description, job.Percent, 100, job.Percent + "%",
            job.State switch { FileScanJob.Failed => job.Error, FileScanJob.Cancelled => "Cancelled.", _ => null }),
            job.Cancellation.Cancel);
    }

    /// <summary>
    /// A task queue batch: the rewrites behind a truncation and a backup run there, under the job id
    /// they were queued with. It is done when it is gone from the queue - a rewrite deletes its batch
    /// on success - and failed when the queue says so. Nothing cancels a rewrite once it is queued.
    /// </summary>
    internal void AttachBatch(SharedTaskRef? task, NodeStoreContainer container, IDataStore store, string jobId, string runningLabel, string doneMessage) {
        var typeId = typeof(RewriteTask).FullName!;
        Attach(task, () => {
            if (container.Store?.Datastore != store || store.State != DataStoreState.Open) {
                return new SharedTaskProgress(Failed, null, 0, null, null, "The database was closed before it finished.");
            }
            var batch = store.TaskQueue.GetBatchMeta([], [typeId], [jobId], 0, 1, out _).FirstOrDefault();
            if (batch == null) return new SharedTaskProgress(Done, null, 0, null, null, doneMessage);
            return batch.State switch {
                BatchState.Pending or BatchState.Waiting => new SharedTaskProgress(Running, "Waiting for its turn in the task queue…", 0, null, null, null),
                BatchState.Running => new SharedTaskProgress(Running, runningLabel, 0, null, null, null),
                BatchState.Completed => new SharedTaskProgress(Done, null, 0, null, null, doneMessage),
                BatchState.Cancelled => new SharedTaskProgress(Cancelled, null, 0, null, null, "Cancelled."),
                _ => new SharedTaskProgress(Failed, null, 0, null, null, batch.ErrorMessage ?? "It failed."),
            };
        });
    }

    /// <summary>
    /// Work done inside the command itself - a state snapshot, measuring a cache: the task runs, with
    /// <paramref name="label"/> as what it is doing, until the work returns, and that is what every
    /// session sees, the one that asked included if it reloads meanwhile. Nothing cancels it.
    /// </summary>
    public async Task<T> RunAsync<T>(SharedTaskRef? task, string label, Func<Task<T>> work, Func<T, string?>? doneMessage = null) {
        if (task is null) return await work();
        var state = new SharedTaskProgress(Running, label, 0, null, null, null);
        Attach(task, () => state);
        try {
            var result = await work();
            state = new SharedTaskProgress(Done, null, 0, null, null, doneMessage?.Invoke(result));
            return result;
        } catch (Exception error) {
            state = new SharedTaskProgress(Failed, null, 0, null, null, error.Message);
            throw;
        }
    }
    public T Run<T>(SharedTaskRef? task, string label, Func<T> work, Func<T, string?>? doneMessage = null) =>
        RunAsync(task, label, () => Task.FromResult(work()), doneMessage).GetAwaiter().GetResult();

    /// <summary>What the board shows, oldest first. Also where finished tasks are noticed and old ones let go of.</summary>
    public List<SharedTaskInfo> List() {
        var now = _clock.ElapsedMilliseconds;
        var list = new List<SharedTaskInfo>();
        lock (_tasks) {
            foreach (var t in _tasks.Values.ToArray()) {
                var (state, side) = decide(t, now);
                if (state.Status == Running) {
                    t.FinishedUtc = null;
                } else {
                    t.FinishedUtc ??= DateTime.UtcNow;
                    if ((DateTime.UtcNow - t.FinishedUtc.Value).TotalMilliseconds > keepFinishedMs) {
                        _tasks.Remove(t.Id);
                        continue;
                    }
                }
                list.Add(new SharedTaskInfo(t.Id, t.Key, t.StoreId, t.Title, t.StartedBy, t.StartedUtc,
                    state.Status, state.Label, state.Done, state.Total, state.Meta, state.Message, t.FinishedUtc, cancellable(t, state, side)));
            }
        }
        return [.. list.OrderBy(t => t.StartedUtc)];
    }

    // ---- the two sides of a task ----

    // The job decides while it runs. Once it is over, the browser decides if it has said anything
    // since - it may be on to a step the job does not cover, or reporting how it all ended - and
    // otherwise the job's last word stands: a tab that went quiet before the job ended (reloaded,
    // closed) has nothing to add. With no job at all the browser decides while it is there, and a
    // task the browser ran alone and then left is the one case nobody can finish: it says so.
    // Side says which of the two the state came from: that is the one a Cancel goes to.
    enum Side { None, Server, Browser }
    (SharedTaskProgress State, Side Side) decide(SharedTask t, long now) {
        pollIfDue(t, now);
        if (t.Server?.Status == Running) return (t.Server, Side.Server);
        var browserThere = t.Browser != null && browserIsThere(t, now);
        if (t.Server != null) return browserThere && t.BrowserSeenMs > t.ServerEndedMs ? (t.Browser!, Side.Browser) : (t.Server, Side.Server);
        if (browserThere) return (t.Browser!, Side.Browser);
        if (t.Browser == null) return (new SharedTaskProgress(Running, null, 0, null, null, null), Side.None);
        if (t.Browser.Status != Running) return (t.Browser, Side.None);
        return (t.Browser with { Status = Failed, Message = "Stopped: the browser tab that was running it was closed or reloaded." }, Side.None);
    }
    // a finished job is not asked again: what it said last is its last word
    static void pollIfDue(SharedTask t, long now) {
        if (t.Poll == null || (t.Server != null && t.Server.Status != Running) || now - t.PolledMs < pollEveryMs) return;
        try {
            t.Server = t.Poll();
        } catch (Exception error) {
            t.Server = new SharedTaskProgress(Failed, null, 0, null, null, error.Message);
        }
        t.PolledMs = now;
        if (t.Server.Status != Running) t.ServerEndedMs = now;
    }
    static bool browserIsThere(SharedTask t, long now) => !t.BrowserLeft && now - t.BrowserSeenMs < browserSilenceMs;
    static bool cancellable(SharedTask t, SharedTaskProgress state, Side side) =>
        state.Status == Running && (side == Side.Server ? t.Cancel != null : side == Side.Browser);

    object report(SharedTaskReport p, HttpContext http) {
        if (p.Id == Guid.Empty) throw new Exception("A task needs an id. ");
        lock (_tasks) {
            var t = getOrAdd(p.Id, p.StoreId, p.Title ?? "Task", userOf(http));
            if (!string.IsNullOrWhiteSpace(p.Title)) t.Title = p.Title; // the dialog's title is the one people saw
            t.Key ??= p.Key;
            t.StoreId ??= p.StoreId;
            // reports travel on separate requests, so one can overtake another - the tab's last one
            // overtaken by a report it sent just before, say: only a newer one counts
            if (p.Seq > 0) {
                if (p.Seq <= t.BrowserSeq) return new { t.CancelRequested };
                t.BrowserSeq = p.Seq;
            }
            var status = normalStatus(p.Status);
            if (p.Left) {
                // the tab is going away: whatever it was doing itself stops with it, a job on the
                // server goes on and has the last word (see decide)
                t.Browser = status == Running
                    ? new SharedTaskProgress(Failed, p.Label, p.Done, p.Total, p.Meta, "Stopped: the browser tab that was running it was closed or reloaded.")
                    : new SharedTaskProgress(status, p.Label, p.Done, p.Total, p.Meta, p.Message);
                t.BrowserLeft = true;
                return new { t.CancelRequested };
            }
            t.Browser = new SharedTaskProgress(status, p.Label, p.Done, p.Total, p.Meta, p.Message);
            t.BrowserSeenMs = _clock.ElapsedMilliseconds;
            t.BrowserLeft = false; // a page put back from the browser's cache carries on where it was
            return new { t.CancelRequested };
        }
    }

    object cancel(Guid id) {
        Action? stop = null;
        lock (_tasks) {
            if (!_tasks.TryGetValue(id, out var t)) throw new Exception("The task is not running any more. ");
            var (state, side) = decide(t, _clock.ElapsedMilliseconds);
            if (!cancellable(t, state, side)) throw new Exception("This task cannot be cancelled. ");
            if (side == Side.Server) stop = t.Cancel;
            else t.CancelRequested = true; // the browser running it hears about it with its next report
        }
        stop?.Invoke();
        return new { Cancelled = true };
    }

    SharedTask getOrAdd(Guid id, Guid? storeId, string title, string? startedBy) {
        if (_tasks.TryGetValue(id, out var t)) return t;
        t = new SharedTask(id) { StoreId = storeId, Title = title, StartedBy = startedBy };
        _tasks[id] = t;
        return t;
    }
    static string normalStatus(string? status) => status is Done or Failed or Cancelled ? status : Running;
    // who to name as having started it: the signed in user, or the machine itself under the localhost bypass
    string? userOf(HttpContext http) {
        var (userName, viaLocalhost, _) = _server.Authentication.Describe(http);
        return userName ?? (viaLocalhost ? "localhost" : null);
    }

    sealed class SharedTask(Guid id) {
        public Guid Id { get; } = id;
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public string? Key;
        public Guid? StoreId;
        public string Title = "";
        public string? StartedBy;
        // the browser side: the dialog's own state, and when it last said so
        public SharedTaskProgress? Browser;
        public long BrowserSeenMs;
        public long BrowserSeq;
        public bool BrowserLeft;
        public bool CancelRequested;
        // the server side: the job, and what it said last
        public Func<SharedTaskProgress>? Poll;
        public Action? Cancel;
        public SharedTaskProgress? Server;
        public long PolledMs;
        public long ServerEndedMs; // when the job was first seen to be over
        public DateTime? FinishedUtc;
    }
}
/// <summary>Where a task stands: Status is running, done, error or cancelled; Total null for "no idea how far".</summary>
public sealed record SharedTaskProgress(string Status, string? Label, double Done, double? Total, string? Meta, string? Message);
/// <summary>The task a command runs as: the id its dialog gave it, the database it is about, a title and who started it.</summary>
public readonly record struct SharedTaskRef(Guid Id, Guid? StoreId, string Title, string? StartedBy);
public sealed record SharedTaskInfo(Guid Id, string? Key, Guid? StoreId, string Title, string? StartedBy, DateTime StartedUtc,
    string Status, string? Label, double Done, double? Total, string? Meta, string? Message, DateTime? FinishedUtc, bool Cancellable);
/// <summary>Seq numbers a tab's reports on one task, so an older one arriving late is ignored; Left is its last, sent as the page goes away.</summary>
sealed record SharedTaskReport(Guid Id, string? Key, Guid? StoreId, string? Title, string? Status, string? Label, double Done, double? Total, string? Meta, string? Message,
    bool Left = false, long Seq = 0);
sealed record SharedTaskIdPayload(Guid Id = default, Guid? TaskId = null);
