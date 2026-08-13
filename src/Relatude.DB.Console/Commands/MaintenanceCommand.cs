using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.NodeServer;

namespace Relatude.DB.Cli.Commands;

/// <summary>
/// Housekeeping that otherwise needs the admin UI: flushing, truncating the log, writing state files,
/// backing up and forcing an index rebuild.
///
/// <para>Truncating and backing up are done by calling the rewrite directly rather than by queueing the
/// task the server would queue: a command line tool has to finish its work before it exits, and the task
/// queue is not running while it does.</para>
/// </summary>
public static class MaintenanceCommand {
    public static async Task<int> RunAsync(CommandArgs args) {
        args.Accept([.. Target.Options, .. ModelSource.Options, .. StoreHost.Options,
            "yes", "truncate", "keep-forever", "delete-old"]);
        var action = (args.SinglePositional("action") ?? throw new UsageException(
            "Which action? flush, truncate-log, save-state, update-caches, clear-cache, backup or reset-indexes."))
            .ToLowerInvariant();
        var target = Target.Resolve(args);
        if (action == "reset-indexes") return resetIndexes(args, target);

        using var host = await StoreHost.OpenAsync(args, target);
        var store = host.Store;
        var datastore = store.Datastore;
        var before = host.Info();
        switch (action) {
            case "flush":
                await store.MaintenanceAsync(MaintenanceAction.FlushDisk);
                Output.WriteLine("Flushed to disk.");
                break;
            case "truncate-log": {
                    if (before.LogTruncatableActions == 0) {
                        Output.WriteLine("Nothing to truncate: the log holds the current state only.");
                        break;
                    }
                    Output.Info("Truncating " + Output.Bytes(before.LogFileSize) + " of log, "
                        + before.LogTruncatableActions.ToString("N0") + " action(s) can go...");
                    var newKey = datastore.FileKeys.WAL_NextFileKey(datastore.IO);
                    datastore.RewriteStore(true, newKey, datastore.IO); // hot swap: the store continues on the new file
                    var deleted = args.Flag("delete-old") ? datastore.DeleteOldLogs() : 0;
                    var after = host.Info();
                    Output.WriteLine("Log truncated: " + Output.Bytes(before.LogFileSize) + " -> " + Output.Bytes(after.LogFileSize)
                        + "  (" + (after.LogFileKey ?? newKey) + ")");
                    Output.WriteLine(deleted > 0 ? deleted + " old log file(s) deleted."
                        : "The previous log file is kept" + (args.Flag("delete-old") ? "." : ", pass --delete-old to remove it."));
                    break;
                }
            case "save-state":
                datastore.SaveIndexStates(false, false);
                Output.WriteLine("State and index files written, so the next start reads them instead of replaying the log.");
                break;
            case "update-caches":
                await store.MaintenanceAsync(MaintenanceAction.UpdatePersistedCaches);
                Output.WriteLine("Persisted caches updated.");
                break;
            case "clear-cache":
                await store.MaintenanceAsync(MaintenanceAction.ClearCache | MaintenanceAction.GarbageCollect);
                Output.WriteLine("Caches cleared.");
                break;
            case "backup":
                backup(args, host);
                break;
            default:
                throw new UsageException("Unknown action \"" + action + "\". "
                    + "Use flush, truncate-log, save-state, update-caches, clear-cache, backup or reset-indexes.");
        }
        return 0;
    }

    static void backup(CommandArgs args, StoreHost host) {
        var datastore = host.Store.Datastore;
        var ioId = host.Settings.IoBackup ?? host.Settings.IoDatabase;
        var io = ioId == null || ioId == Guid.Empty ? datastore.IOBackup : host.Server.GetIO(ioId.Value);
        var truncate = args.Flag("truncate");
        var keepForever = args.Flag("keep-forever");
        var fileKey = datastore.FileKeys.WAL_GetFileKeyForBackup(DateTime.UtcNow, keepForever);
        Output.Info("Writing backup to " + fileKey + "...");
        if (truncate) datastore.RewriteStore(false, fileKey, io); // rewritten: current state only
        else datastore.CopyStore(fileKey, io); // copied: the whole history
        var size = io.GetFileSizeOrZeroIfUnknown(fileKey);
        Output.WriteLine("Backup written: " + fileKey + (size > 0 ? "  " + Output.Bytes(size) : string.Empty)
            + (truncate ? "  (current state only)" : "  (full history)")
            + (keepForever ? "  (excluded from backup rotation)" : string.Empty));
    }

    /// <summary>
    /// Deletes the state and index files so the next start rebuilds them from the log. Nothing that is not
    /// derived from the log is touched, but rebuilding a large database is slow, so it is confirmed.
    /// </summary>
    static int resetIndexes(CommandArgs args, Target target) {
        if (!args.Flag("yes")) {
            throw new CliException("reset-indexes deletes the state and index files, which forces a full rebuild "
                + "from the log the next time the database opens. Nothing else is lost. Pass --yes to go ahead.");
        }
        var settings = SettingsReader.Read(target);
        var container = SettingsReader.SelectContainer(settings, target.Store);
        var keys = new FileKeyUtility(container.LocalSettings?.FilePrefix);
        var deleted = 0;
        foreach (var id in new[] { container.IoIndexes, container.IoDatabase }.Distinct()) {
            if (id == null || id == Guid.Empty) continue;
            var ioSettings = (container.IOSettings ?? []).FirstOrDefault(s => s.Id == id.Value);
            if (ioSettings == null) continue;
            IIOProvider io;
            try {
                io = IOSettings.Create(ioSettings, target.Root);
            } catch (Exception err) {
                throw new CliException("Could not open the storage provider \"" + ioSettings.Name + "\": " + err.Message, err);
            }
            io.DeleteFolderIfItExists([keys.IndexStoreFolderKey]);
            foreach (var key in new[] { keys.StateFileKey }.Concat(keys.MapperDll_GetAllFileKeys(io)).Concat(keys.Index_GetAll(io))) {
                if (io.DoesNotExistOrIsEmpty(key)) continue;
                io.DeleteFileIfItExists(key);
                deleted++;
                Output.Detail("Deleted " + key);
            }
        }
        Output.WriteLine("Deleted " + deleted + " state and index file(s). The next start rebuilds them from the log.");
        return 0;
    }
}
