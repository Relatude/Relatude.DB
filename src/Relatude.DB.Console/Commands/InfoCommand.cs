namespace Relatude.DB.Cli.Commands;

/// <summary>Opens a database and reports what is in it and how it is doing.</summary>
public static class InfoCommand {
    public static async Task<int> RunAsync(CommandArgs args) {
        args.Accept([.. Target.Options, .. ModelSource.Options, .. StoreHost.Options]);
        var target = Target.Resolve(args);
        using var host = await StoreHost.OpenAsync(args, target);
        var info = host.Info();
        var settings = host.Settings;
        if (args.Flag("json")) {
            Output.Json(new {
                Database = settings.Name,
                settings.Id,
                State = host.Store.State.ToString(),
                SettingsFile = target.SettingsPath,
                ContentRoot = target.Root,
                Info = info,
            });
            return 0;
        }
        Output.WriteLine($"Database \"{settings.Name}\"  {host.Store.State}");
        Output.Table([
            ("id", settings.Id.ToString()),
            ("settings", target.SettingsPath),
            ("content root", target.Root),
            ("opened in", info.StartUpMs.ToString("N0") + " ms"),
        ]);
        Output.WriteLine();
        Output.WriteLine("Contents");
        Output.Table([
            ("nodes", info.NodeCount.ToString("N0")),
            ("relations", info.RelationCount.ToString("N0")),
            ("node types", info.DatamodelNodeTypeCount.ToString("N0")),
            ("properties", info.DatamodelPropertyCount.ToString("N0")),
            ("relation types", info.DatamodelRelationCount.ToString("N0")),
            ("indexes", info.DatamodelIndexCount.ToString("N0")),
        ]);
        var counts = info.TypeCounts.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ToList();
        if (counts.Count > 0) {
            Output.WriteLine();
            Output.WriteLine("Nodes per type");
            Output.Table(counts.Select(kv => (kv.Key, kv.Value.ToString("N0"))));
        }
        Output.WriteLine();
        Output.WriteLine("Files");
        Output.Table([
            ("log", (info.LogFileKey ?? "?") + "  " + Output.Bytes(info.LogFileSize)),
            ("state", Output.Bytes(info.LogStateFileSize)),
            ("indexes", Output.Bytes(info.IndexFileSize)),
            ("file store", Output.Bytes(info.FileStoreSize)),
            ("backups", Output.Bytes(info.BackupFileSize)),
            ("logging", Output.Bytes(info.LoggingFileSize)),
            ("total", Output.Bytes(info.TotalFileSize)),
        ]);
        Output.WriteLine();
        Output.WriteLine("Log");
        Output.Table([
            ("first change", time(info.LogFirstStateUtc)),
            ("last change", time(info.LogLastChange)),
            ("actions not in state file", info.LogActionsNotItInStatefile.ToString("N0")),
            ("transactions not in state file", info.LogTransactionsNotItInStatefile.ToString("N0")),
            ("truncatable actions", info.LogTruncatableActions.ToString("N0")),
            ("indexes out of sync", info.NoIndexesOutOfSync.ToString("N0")),
        ]);
        if (info.LogTruncatableActions > 0) {
            Output.Info("The log holds " + info.LogTruncatableActions.ToString("N0")
                + " action(s) that a truncate would remove: relatude maintenance truncate-log");
        }
        var pending = info.QueuedTasksPending + info.QueuedTasksPendingPersisted;
        if (pending > 0) {
            Output.WriteLine();
            Output.WriteLine("Background work");
            Output.Table([
                ("pending tasks", pending.ToString("N0")),
                ("pending batches", (info.QueuedBatchesPending + info.QueuedBatchesPendingPersisted).ToString("N0")),
            ]);
            if (!args.Flag("allow-background")) {
                Output.Info("The task queue is not running: this tool disables it unless --allow-background is given.");
            }
        }
        var log = host.Server.GetStartUpLog();
        if (Output.Verbose && log.Length > 0) {
            Output.WriteLine();
            Output.WriteLine("Start up log");
            foreach (var (time, text) in log) Output.WriteLine("  " + time.ToString("HH:mm:ss.fff") + "  " + text);
        }
        return 0;
    }
    static string time(DateTime? value)
        => value == null || value == default(DateTime) ? "-" : value.Value.ToString("u");
}
