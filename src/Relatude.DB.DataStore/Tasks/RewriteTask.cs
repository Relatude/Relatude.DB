using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using System.Diagnostics;

namespace Relatude.DB.Tasks;

public class RewriteTask : TaskData {
    public bool HotSwapToNewFile;
    public bool DeleteOldDbFilesAfterHotSwap;
    required public string[]? NewLogFileKey;
    required public IIOProvider IO;
    public bool IsBackup;
    public bool Truncate = true;
}
public class RewriteTaskRunner(IDataStore db) : TaskRunner<RewriteTask> {
    public override BatchTaskPriority Priority => BatchTaskPriority.High;
    public override async Task ExecuteAsync(Batch<RewriteTask> batch, TaskLogger? taskLogger) {
        foreach (var t in batch.Tasks) Run(db, t);
    }
    /// <summary>One rewrite or backup, on the calling thread: what a batch of these does, and what the
    /// built-in jobs "backup" and "truncate-log" do without queueing a batch of their own.</summary>
    public static void Run(IDataStore db, RewriteTask t) {
        if (t.NewLogFileKey == null) {
            t.NewLogFileKey = FileKeyUtility.WAL_NextFileKey(db.IO);
        }
        var sw = Stopwatch.StartNew();
        if (t.IsBackup) {
            db.Log(SystemLogEntryType.Backup, "Backup started: " + t.NewLogFileKey.AsKeyString());
        } else {
            db.Log(SystemLogEntryType.Info, "Rewrite started: " + t.NewLogFileKey.AsKeyString());
        }
        if (t.Truncate) {
            db.RewriteStore(t.HotSwapToNewFile, t.NewLogFileKey, t.IO);
        } else {
            db.CopyStore(t.NewLogFileKey, t.IO);
        }
        if (t.DeleteOldDbFilesAfterHotSwap && t.HotSwapToNewFile) {
            db.DeleteOldLogs();
        }
        sw.Stop();
        if (t.IsBackup) {
            db.Log(SystemLogEntryType.Backup, $"Backup completed in " + sw.ElapsedMilliseconds.To1000N() + "ms. ");
        } else {
            db.Log(SystemLogEntryType.Info, $"Rewrite completed in " + sw.ElapsedMilliseconds.To1000N() + "ms. ");
        }
    }
    public override bool PersistToDisk => false;
    public override bool DeleteOnSuccess => true;
    public override int MaxTaskCountPerBatch => 1;
    // a rewrite or copy refuses to start while another one runs, so a second one alongside would
    // fail rather than wait: one at a time, whatever the settings say
    public override int MaxConcurrency => 1;
    public override int MaxConcurrencyLimit => 1;
    public override TimeSpan GetMaximumAgeInQueueAfterExecution() => TimeSpan.FromHours(24);
    // the file it writes, known once it has run when the task left it to the runner to pick
    public override string? GetTaskLogId(RewriteTask task) => task.NewLogFileKey?.AsKeyString();
    public override string? DescribeTask(RewriteTask task) => task.IsBackup
        ? (task.Truncate ? "truncated backup" : "backup")
        : task.Truncate ? (task.HotSwapToNewFile ? "log truncation" : "truncated copy") : "copy";
    public override RewriteTask TaskFromBytes(byte[] bytes) => throw new NotImplementedException();
    public override byte[] TaskToBytes(RewriteTask task) => throw new NotImplementedException();
}
