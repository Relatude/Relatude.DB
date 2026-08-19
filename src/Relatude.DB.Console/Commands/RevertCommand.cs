using System.Globalization;

namespace Relatude.DB.Cli.Commands;

/// <summary>
/// The experiment workflow: remember the head of the transaction log before making changes
/// ("relatude timestamp"), change and test freely, then put the database back by permanently
/// deleting every transaction after the remembered point ("relatude revert --after &lt;timestamp&gt; --yes").
///
/// <para>The revert runs the store's own DeleteTransactionsAfter: the log is truncated at the
/// timestamp and the database reloads. Persisted state that advanced past the point (state
/// snapshot, index engines) is reset and rebuilt from the log, which the command reports.</para>
/// </summary>
public static class RevertCommand {

    public static async Task<int> TimestampAsync(CommandArgs args) {
        args.Accept([.. Target.Options, .. ModelSource.Options, .. StoreHost.Options]);
        var target = Target.Resolve(args);
        using var host = await StoreHost.OpenAsync(args, target);
        var ts = host.Store.Datastore.GetLastTimestampID();
        if (args.Flag("json")) {
            Output.Json(new { Timestamp = ts, Utc = new DateTime(ts, DateTimeKind.Utc) });
        } else {
            Output.Info("Head of the transaction log (UTC " + new DateTime(ts, DateTimeKind.Utc).ToString("o") + "):");
            Output.WriteLine(ts.ToString()); // the bare number on stdout, so a script can capture it
        }
        return 0;
    }

    public static async Task<int> RunAsync(CommandArgs args) {
        args.Accept([.. Target.Options, .. ModelSource.Options, .. StoreHost.Options, "after", "yes", "dry-run"]);
        var target = Target.Resolve(args);
        var raw = args.Get("after") ?? args.SinglePositional("timestamp")
            ?? throw new UsageException("Which point to revert to? Pass --after <timestamp>, a value taken with \"relatude timestamp\" before the changes.");
        var after = parseTimestamp(raw);
        using var host = await StoreHost.OpenAsync(args, target);
        var datastore = host.Store.Datastore;
        var dryRun = args.Flag("dry-run");

        // always look first, so the command can say what is about to happen (and refuse without --yes)
        var preview = datastore.DeleteTransactionsAfter(after, dryRun: true);
        if (preview.TransactionsDeleted == 0) {
            if (args.Flag("json")) Output.Json(preview);
            else Output.WriteLine("Nothing to revert: no transactions after " + describe(after) + ".");
            return 0;
        }
        Output.WriteLine((dryRun ? "Would delete " : "Deleting ") + preview.TransactionsDeleted.ToString("N0")
            + " transaction(s) with " + preview.ActionsDeleted.ToString("N0") + " action(s), "
            + Output.Bytes(preview.BytesTruncated) + " of log: everything after " + describe(after) + ".");
        if (dryRun) {
            if (args.Flag("json")) Output.Json(preview);
            return 0;
        }
        if (!args.Flag("yes")) {
            throw new CliException("This permanently deletes the transactions listed above, as if they never happened. "
                + "Pass --yes to go ahead, or --dry-run to only look.");
        }
        var result = datastore.DeleteTransactionsAfter(after);
        if (args.Flag("json")) {
            Output.Json(result);
        } else {
            Output.WriteLine("Reverted to " + describe(result.LastTimestamp) + " in " + result.DurationMs.ToString("N0") + " ms.");
            if (result.StateAndIndexesReset) Output.WriteLine("The state snapshot was newer than the revert point: state and indexes were rebuilt from the log.");
            foreach (var name in result.EnginesReset) Output.WriteLine("Index engine \"" + name + "\" was reset and rebuilt from the log.");
        }
        return 0;
    }

    static string describe(long timestamp) => timestamp + " (UTC " + new DateTime(timestamp, DateTimeKind.Utc).ToString("o") + ")";

    /// <summary>The number from "relatude timestamp", or a UTC date/time for the human reaching for a clock.</summary>
    static long parseTimestamp(string value) {
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return ticks;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) return utc.Ticks;
        throw new UsageException("\"" + value + "\" is not a timestamp. Pass the number from \"relatude timestamp\", or a UTC date/time like 2026-08-19T14:30:00Z.");
    }
}
