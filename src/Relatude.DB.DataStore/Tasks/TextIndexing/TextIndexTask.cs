using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Transactions;
namespace Relatude.DB.Tasks.TextIndexing;

public class TextIndexTask(int nodeId) : TaskData {
    public int NodeId { get; } = nodeId;
}
public class TextIndexTaskRunner(IDataStore db) : TaskRunner<TextIndexTask> {
    public override BatchTaskPriority Priority => BatchTaskPriority.Low;
    public override int MaxTaskCountPerBatch => 200;
    public override bool PersistToDisk => true; //Random.Shared.Next(0, 100) < 50;
    // one at a time: the index is written under the database's write lock, so a second batch mostly
    // waits for the first - and takes that lock from the transactions queueing the work meanwhile
    public override int MaxConcurrency => 1;
    // the extract is read first and written later, so two batches holding the same node must not
    // overlap: the one that read the older text could finish last and leave it in the index
    public override string? GetConcurrencyKey(TextIndexTask task) => task.NodeId.ToString();
    public override async Task ExecuteAsync(Batch<TextIndexTask> batch, TaskLogger? taskLogger) {
        var ids = batch.Tasks.Select(t => t.NodeId).ToArray();
        var extracts = db.GetTextExtract(ids, TextIndexType.PlainTextSearch);
        var t = new TransactionData();
        foreach (var extract in extracts) {
            t.ForceUpdateProperty(extract.NodeId, NodeConstants.SystemTextIndexPropertyId, extract.RevisionId, extract.Text);
        }
        db.Execute(t);
    }
    public override bool DeleteOnSuccess => true;
    public override TimeSpan GetMaximumAgeInQueueAfterExecution() => TimeSpan.FromHours(1);
    public override bool RestartTaskBatchesOnStartupThatStartedButNeverFailedOrCompleted { get; } = true;
    public override TextIndexTask TaskFromBytes(byte[] bytes) {
        return new TextIndexTask(BitConverter.ToInt32(bytes, 0));
    }
    public override byte[] TaskToBytes(TextIndexTask task) {
        return BitConverter.GetBytes(task.NodeId);
    }
}
