using System.Globalization;
using Relatude.DB.Common;
using Relatude.DB.DataStores;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The revert window of one database, as the admin UI drives it: begin, commit, roll back, and
/// the status the page shows while one is open. Everything here is a thin layer over
/// <see cref="IDataStore.BeginRevertWindow"/> and friends; what it adds is the wording and the
/// numbers a person needs before pressing a button that deletes transactions.
///
/// Timestamps travel as strings. They are log timestamps (UTC ticks), well past 2^53, and a
/// javascript number would round them; the client only ever hands them straight back.
///
/// The container broadcast carries whether a window is open (see UIServer.buildContainers), so
/// every page of every connected UI learns about a window begun elsewhere - from code, from the
/// CLI, from another browser - without polling for it. This class only answers when asked.
/// </summary>
sealed class UIRevert {
    readonly RelatudeDBServer _server;
    internal UIRevert(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("revert-status", ctx => status(ctx.Payload<StorePayload>().StoreId));
        commands.Register("revert-begin", ctx => begin(ctx.Payload<BeginPayload>()));
        commands.Register("revert-commit", ctx => commit(ctx.Payload<StorePayload>().StoreId));
        commands.Register("revert-preview", ctx => preview(ctx.Payload<StorePayload>().StoreId));
        commands.Register("revert-rollback", ctx => rollback(ctx.Payload<StorePayload>().StoreId));
    }

    IDataStore store(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        if (c.Store == null || c.Store.State != DataStoreState.Open) throw new Exception("The database is not open. ");
        return c.Store.Datastore;
    }

    /// <summary>
    /// Where the window stands. Cheap enough to poll: two reads under the store's read lock and no
    /// file access. "Changed since begin" is the head of the log having moved past the window's
    /// timestamp - a yes/no, not a count; counting means scanning the log, which is what
    /// <see cref="preview"/> does when someone is about to roll back.
    /// </summary>
    object status(Guid storeId) {
        var ds = store(storeId);
        var window = ds.RevertWindow;
        var head = ds.GetLastTimestampID();
        return new {
            StoreId = storeId,
            Active = window != null,
            Window = window == null ? null : describe(window),
            HeadTimestamp = ticks(head),
            HeadUtc = utc(head),
            ChangedSinceBegin = window != null && head > window.Timestamp,
        };
    }

    object begin(BeginPayload p) {
        var ds = store(p.StoreId);
        ds.BeginRevertWindow(p.SaveStateFirst);
        return status(p.StoreId);
    }

    object commit(Guid storeId) {
        var ds = store(storeId);
        ds.CommitRevertWindow();
        return status(storeId);
    }

    /// <summary>What a rollback would delete, so the confirmation can name it. A dry run against
    /// the window's own timestamp: the store scans the log tail (from the snapshot the window
    /// wrote when it began, so a short one) and changes nothing.</summary>
    object preview(Guid storeId) {
        var ds = store(storeId);
        var window = ds.RevertWindow ?? throw new Exception("No revert window is active. ");
        return describe(ds.DeleteTransactionsAfter(window.Timestamp, dryRun: true));
    }

    /// <summary>The destructive one. Runs on the request: the store holds its write lock while it
    /// truncates the log and reloads, and there is nothing useful to report until it is done.</summary>
    object rollback(Guid storeId) {
        var ds = store(storeId);
        var result = ds.RollbackRevertWindow();
        return new { Result = describe(result), Status = status(storeId) };
    }

    static object describe(RevertWindowInfo w) => new {
        Timestamp = ticks(w.Timestamp),
        TimestampUtc = w.TimestampUtc,
        w.BegunUtc,
        w.LogPosition,
    };

    static object describe(DeleteTransactionsResult r) => new {
        r.DryRun,
        AfterUtc = utc(r.AfterTimestamp),
        LastUtc = utc(r.LastTimestamp),
        r.TransactionsDeleted,
        r.ActionsDeleted,
        r.BytesTruncated,
        r.StateAndIndexesReset,
        r.EnginesReset,
        r.DurationMs,
    };

    static string ticks(long timestamp) => timestamp.ToString(CultureInfo.InvariantCulture);
    static DateTime? utc(long timestamp) => timestamp > 0 ? new DateTime(timestamp, DateTimeKind.Utc) : null;

    sealed record StorePayload(Guid StoreId);
    sealed record BeginPayload(Guid StoreId, bool SaveStateFirst = true);
}
