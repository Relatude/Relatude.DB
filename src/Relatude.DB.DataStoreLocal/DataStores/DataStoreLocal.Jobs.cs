using Relatude.DB.Common;
using Relatude.DB.IO;
using Relatude.DB.Tasks;

namespace Relatude.DB.DataStores;

/// <summary>
/// The jobs of the database (see <see cref="Tasks.Jobs"/>): the scheduler that runs them at
/// the times <see cref="SettingsLocal.Schedules"/> sets, and the jobs the database brings along - the
/// maintenance the admin UI offers as buttons, here so it can be put on a timetable.
/// </summary>
public sealed partial class DataStoreLocal : IDataStore {
    Jobs _jobs = default!;
    public Jobs Jobs => _jobs;
    /// <summary>Off, nothing ticks the scheduler but a call to <see cref="Jobs.Tick"/>: for the tests,
    /// which move the clock themselves. Read when the store opens.</summary>
    internal bool TickSchedulesInBackground { get; set; } = true;

    // from the constructor, while the store is closed and runners can still be registered. The
    // scheduler remembers its schedules in the state folder, beside the persisted queue
    void initJobs() {
        _jobs = new Jobs(this, () => _settings.Schedules, _ioIndex, FileKeyUtility.Scheduler_GetStateFileKey());
        RegisterRunner(new RunJobRunner(_jobs));
        registerBuiltInJobs();
    }

    void registerBuiltInJobs() {
        _jobs.AddBuiltIn("backup", ctx => {
            var args = arguments(ctx, "keep", "truncate", "copy");
            var keep = args.Contains("keep");
            var truncate = args.Contains("truncate") || (!args.Contains("copy") && _settings.TruncateBackups);
            var key = FileKeyUtility.WAL_GetFileKeyForBackup(DateTime.UtcNow, keep);
            RewriteTaskRunner.Run(this, new RewriteTask {
                HotSwapToNewFile = false,
                DeleteOldDbFilesAfterHotSwap = false,
                NewLogFileKey = key,
                IO = _ioAutoBackup,
                Truncate = truncate,
                IsBackup = true,
            });
            ctx.Report((truncate ? "Truncated backup" : "Backup") + " written to " + key.AsKeyString() + ", " + _ioAutoBackup.GetFileSizeOrZeroIfUnknown(key).ToByteString() + (keep ? ", kept forever" : ""));
            return Task.CompletedTask;
        },
        "Writes a backup of the database to the backup storage: a copy of the transaction log, or of the current state when backups are set to be truncated (Settings › Maintenance › Backup). It leaves older backups alone; delete-old-backups thins them out.",
        "keep: never deleted by delete-old-backups or the automatic backup. truncate or copy: overrides the Truncate backups setting for this backup. Combine with a comma: keep,truncate.");

        _jobs.AddBuiltIn("delete-old-backups", ctx => {
            arguments(ctx);
            var deleted = _scheduler.DeleteOlderBackups();
            ctx.Report(deleted == 0 ? "No backup to delete." : "Deleted " + deleted + " backup" + (deleted == 1 ? "" : "s") + ".");
            return Task.CompletedTask;
        },
        "Deletes the backups the retention settings do not keep - the hourly, daily, weekly, monthly and yearly copies kept, under Settings › Maintenance › Backup. A backup marked keep is never deleted.");

        _jobs.AddBuiltIn("truncate-log", ctx => {
            var args = arguments(ctx, "delete-old");
            if (RevertWindowIsActive) {
                ctx.Report("Skipped: a revert window is open, and truncating would give up the history it can return to.");
                return Task.CompletedTask;
            }
            if (GetNoPrimitiveActionsInLogThatCanBeTruncated() <= 0) {
                ctx.Report("Nothing to truncate.");
                return Task.CompletedTask;
            }
            var deleteOld = args.Contains("delete-old");
            RewriteTaskRunner.Run(this, new RewriteTask {
                HotSwapToNewFile = true,
                DeleteOldDbFilesAfterHotSwap = deleteOld,
                NewLogFileKey = null,
                IO = _io,
                Truncate = true,
            });
            ctx.Report("The log was rewritten as the current state" + (deleteOld ? " and the old log file deleted." : "; the old log file is kept."));
            return Task.CompletedTask;
        },
        "Rewrites the transaction log as the current state, reclaiming the space taken by history - and giving up that history, including the ability to revert into it. Take a backup first if it matters.",
        "delete-old: deletes the old log file once the new one is in place. Without it the old file stays on disk.");

        _jobs.AddBuiltIn("delete-old-log-files", ctx => {
            arguments(ctx);
            var deleted = DeleteOldLogs();
            ctx.Report(deleted == 0 ? "No old log file to delete." : "Deleted " + deleted + " old log file" + (deleted == 1 ? "" : "s") + ".");
            return Task.CompletedTask;
        },
        "Deletes the log files a truncation left behind: every file in the data folder but the one in use.");

        _jobs.AddBuiltIn("save-state", ctx => {
            arguments(ctx);
            SaveIndexStates();
            ctx.Report("The state snapshot is up to date.");
            return Task.CompletedTask;
        },
        "Writes the state snapshot and the index states, so the next open reads them instead of replaying the log. The database also does this on its own (Settings › Performance › Index snapshots).");

        _jobs.AddBuiltIn("purge-caches", ctx => {
            arguments(ctx);
            var before = _nodes.CacheSize + _definition.Sets.CacheSize;
            Maintenance(MaintenanceAction.PurgeCache);
            var after = _nodes.CacheSize + _definition.Sets.CacheSize;
            ctx.Report("Caches " + before.ToByteString() + " -> " + after.ToByteString() + ".");
            return Task.CompletedTask;
        },
        "Halves the node and set caches and gives the memory back. The database does this on its own as the caches fill; on a timetable it frees memory at a quiet hour.");

        _jobs.AddBuiltIn("reindex-text", ctx => {
            arguments(ctx);
            var count = ReIndexAllText();
            ctx.Report("Queued " + count.To1000N() + " node" + (count == 1 ? "" : "s") + " for text indexing.");
            return Task.CompletedTask;
        },
        "Queues every node of the text-indexed types for text indexing again. The indexing itself happens in the task queue afterwards, one task per node.");

        _jobs.AddBuiltIn("delete-unreferenced-files", async ctx => {
            var countOnly = arguments(ctx, "count").Contains("count");
            var result = await DeleteUnreferencedFilesAsync(countOnly, (text, percentage) => ctx.Progress(percentage, text), ctx.CancellationToken);
            ctx.Report((countOnly ? "Found " : "Deleted ") + result.TotalFilesDeleted.To1000N() + " unreferenced file" + (result.TotalFilesDeleted == 1 ? "" : "s")
                + ", " + result.TotalBytesDeleted.ToByteString() + (countOnly ? " (nothing deleted)." : "."));
        },
        "Deletes the files in the file stores that no node refers to any more - a file of an older version of a node counts as unreferenced. Files uploaded in the last 15 minutes are left alone.",
        "count: only counts them, deleting nothing.");
    }

    /// <summary>The words of a built-in job's argument, lower case; throws for a word the job does not take,
    /// so a typo in a schedule fails its run visibly rather than being ignored.</summary>
    static HashSet<string> arguments(JobContext ctx, params string[] allowed) {
        var words = (ctx.Argument ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant()).ToHashSet();
        var unknown = words.Where(w => !allowed.Contains(w)).ToArray();
        if (unknown.Length > 0) {
            throw new ArgumentException("\"" + ctx.JobName + "\" " + (allowed.Length == 0 ? "takes no argument" : "takes " + string.Join(", ", allowed))
                + ", not " + string.Join(", ", unknown.Select(w => "\"" + w + "\"")) + ".");
        }
        return words;
    }
}
