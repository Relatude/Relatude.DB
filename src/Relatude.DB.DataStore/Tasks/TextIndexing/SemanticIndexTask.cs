using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Transactions;
namespace Relatude.DB.Tasks.TextIndexing;

public class SemanticIndexTask(int nodeId) : TaskData {
    public int NodeId { get; } = nodeId;
}
public class SemanticIndexTaskRunner(IDataStore db, AIEngine ai) : TaskRunner<SemanticIndexTask> {
    public override BatchTaskPriority Priority => BatchTaskPriority.Low;
    public override int MaxTaskCountPerBatch => 50;
    public override bool PersistToDisk => true;
    // most of a batch is spent waiting for the AI service, outside every lock, so batches running side
    // by side are close to that many times faster - until the service's rate limit, which the
    // providers back off from and retry
    public override int MaxConcurrency => 4;
    // the text is read before the embeddings are fetched and written after, so two batches holding
    // the same node must not overlap: the one that read the older text could finish last
    public override string? GetConcurrencyKey(SemanticIndexTask task) => task.NodeId.ToString();
    public override async Task ExecuteAsync(Batch<SemanticIndexTask> batch, TaskLogger? taskLogger) {
        var ids = batch.Tasks.Select(t => t.NodeId).ToArray();
        var extracts = db.GetTextExtract(ids, TextIndexType.SemanticTextSearch);
        var texts = extracts.Select(t => t.Text).ToArray();
        var vectors = await ai.GetEmbeddingsAsync(texts);
        if(extracts.Length != vectors.Count) {
            throw new Exception($"Expected the same number of vectors as extracts. Extracts: {extracts.Length}, Vectors: {vectors.Count}");
        }
        var t = new TransactionData();
        for (int i = 0; i < extracts.Length; i++) {
            t.ForceUpdateProperty(extracts[i].NodeId, NodeConstants.SystemVectorIndexPropertyId, extracts[i].RevisionId, vectors[i]);
        }
        db.Execute(t);
    }
    public override bool DeleteOnSuccess => true;
    public override TimeSpan GetMaximumAgeInQueueAfterExecution() => TimeSpan.FromHours(1);
    public override bool RestartTaskBatchesOnStartupThatStartedButNeverFailedOrCompleted { get; } = true;
    public override SemanticIndexTask TaskFromBytes(byte[] bytes) {
        return new SemanticIndexTask(BitConverter.ToInt32(bytes, 0));
    }
    public override byte[] TaskToBytes(SemanticIndexTask task) {
        return BitConverter.GetBytes(task.NodeId);
    }
}