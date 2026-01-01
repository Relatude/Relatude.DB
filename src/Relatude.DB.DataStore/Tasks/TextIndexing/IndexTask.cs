using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Transactions;
namespace Relatude.DB.Tasks.TextIndexing;
public class IndexTask(int nodeId, bool textIndex, bool vectorIndex) : TaskData {
    public int NodeId { get; } = nodeId;
    public bool TextIndex { get; } = textIndex;
    public bool VectorIndex { get; } = vectorIndex;
}
public class IndexTaskRunner(IDataStore db) : TaskRunner<IndexTask> {
    public override BatchTaskPriority Priority => BatchTaskPriority.Low;
    public override int MaxTaskCountPerBatch => 100;
    public override bool PersistToDisk => true; //Random.Shared.Next(0, 100) < 50;
    public override Task ExecuteAsync(Batch<IndexTask> batch, TaskLogger? taskLogger) {

        // Only text index, update now
        var onlyTextIndex = batch.Tasks.Where(t => t.TextIndex && !t.VectorIndex);
        {
            var ids = onlyTextIndex.Select(b => b.NodeId).ToArray();
            var nodesAndText = db.GetTextExtractsForExistingNodesAndWhereContent(ids);
            var t = new TransactionData();
            foreach (var n in nodesAndText) {
                t.ForceUpdateProperty(n.NodeId, NodeConstants.SystemTextIndexPropertyId, n.Text);
            }
            db.Execute(t);
        }
        var bothIndexes = batch.Tasks.Where(t => t.VectorIndex);
        {
            foreach (var task in bothIndexes) {
                string? text = null;
                if (task.TextIndex) {
                    var idAndTexts = db.GetTextExtractsForExistingNodesAndWhereContent([task.NodeId]);
                    if (idAndTexts.Length > 0) text = idAndTexts.First().Text;
                }
                db.EnqueueTask(new TextOrSemanticIndexTask(task.NodeId, text));
            }
        }
        return Task.CompletedTask;
    }
    public override bool DeleteOnSuccess => true;
    public override TimeSpan GetMaximumAgeInQueueAfterExecution() => TimeSpan.FromHours(1);
    public override bool RestartIfAbortedDuringShutdown { get; set; } = true;
    public override IndexTask TaskFromBytes(byte[] bytes) {
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);
        var nodeId = reader.ReadInt32();
        var textIndex = reader.ReadBoolean();
        var vectorIndex = reader.ReadBoolean();
        return new IndexTask(nodeId, textIndex, vectorIndex);
    }
    public override byte[] TaskToBytes(IndexTask task) {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(task.NodeId);
        writer.Write(task.TextIndex);
        writer.Write(task.VectorIndex);
        return ms.ToArray();
    }
}
