using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using System.Diagnostics;

namespace Relatude.DB.Tasks;

public class RewriteTask : TaskData {
    public bool HotSwapToNewFile;
    public bool DeleteOldDbFilesAfterHotSwap;
    required public string NewLogFileKey;
    required public IIOProvider IO;
    public bool IsBackup;
    public bool Truncate = true;
}
public class RewriteTaskRunner(IDataStore db) : TaskRunner<RewriteTask> {
    public override BatchTaskPriority Priority => BatchTaskPriority.High;
    public override Task ExecuteAsync(Batch<RewriteTask> batch, TaskLogger? taskLogger) {
        foreach (var t in batch.Tasks) {
            var sw = Stopwatch.StartNew();
            if (t.IsBackup) {
                db.Log(SystemLogEntryType.Backup, "Backup started: " + t.NewLogFileKey);
            } else {
                db.Log(SystemLogEntryType.Info, "Rewrite started: " + t.NewLogFileKey);
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
        return Task.CompletedTask;
    }
    public override bool PersistToDisk => false;
    public override bool DeleteOnSuccess => true;
    public override int MaxTaskCountPerBatch => 1;
    public override TimeSpan GetMaximumAgeInQueueAfterExecution() => TimeSpan.FromHours(24);
    public override RewriteTask TaskFromBytes(byte[] bytes) => throw new NotImplementedException();
    public override byte[] TaskToBytes(RewriteTask task) => throw new NotImplementedException();
}
