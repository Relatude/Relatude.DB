using Relatude.DB.AI;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Transactions;
namespace Relatude.DB.Tasks.TextIndexing;
public class TextOrSemanticIndexTask(int nodeId, string? textIndex) : TaskData() {
    public int NodeId { get; } = nodeId;
    public string? TextIndex { get; } = textIndex;
}
public class SemanticIndexTaskRunner(IDataStore db, AIEngine? ai) : TaskRunner<TextOrSemanticIndexTask> {
    public override BatchTaskPriority Priority => BatchTaskPriority.Medium;
    public override int MaxTaskCountPerBatch => 20;
    public override bool PersistToDisk => true;
    public override async Task ExecuteAsync(Batch<TextOrSemanticIndexTask> batch, TaskLogger? taskLogger) {
        var t = new TransactionData();
        var onlySemanticIndex = batch.Tasks.Where(t => t.TextIndex == null).ToList();
        if(onlySemanticIndex.Count > 0) await updateSemanticIndexOnly(onlySemanticIndex, t);
        var withTextIndex = batch.Tasks.Where(t => t.TextIndex != null).ToList();
        if(withTextIndex.Count > 0) await updateTextAndMaybeSemanticIndex(withTextIndex, t);
        if (t.Actions.Count > 0) db.Execute(t);
    }
    async Task updateSemanticIndexOnly(IEnumerable<TextOrSemanticIndexTask> tasks, TransactionData t) {
        if (ai == null) throw new Exception("AI service is not available in this DataStore.");
        var ids = tasks.Select(b => b.NodeId).ToArray();
        var nodesAndSemText = db.GetSemanticTextExtractsForExistingNodesAndWhereContent(ids);
        var texts = nodesAndSemText.Select(n => n.Text).ToArray();
        var vector = await ai.GetEmbeddingsAsync(texts);
        for (int i = 0; i < nodesAndSemText.Length; i++) {
            if (vector[i] == null) continue; // Skip if no vector was generated
            t.UpdateProperty(nodesAndSemText[i].NodeId, NodeConstants.SystemVectorIndexPropertyId, vector[i]);
        }
    }
    async Task updateTextAndMaybeSemanticIndex(IEnumerable<TextOrSemanticIndexTask> tasks, TransactionData t) {
        if (ai == null) throw new Exception("AI service is not available in this DataStore.");
        var ids = tasks.Select(b => b.NodeId).ToArray();
        var nodesAndSemText = db.GetSemanticTextExtractsForExistingNodesAndWhereContent(ids);
        var texts = nodesAndSemText.Select(n => n.Text).ToArray();
        var vectors = await ai.GetEmbeddingsAsync(texts);
        var vectorsByNodeId = new Dictionary<int, float[]>();
        for (int i = 0; i < nodesAndSemText.Length; i++) {
            if (vectors[i] == null) continue; // Skip if no vector was generated
            vectorsByNodeId[nodesAndSemText[i].NodeId] = vectors[i];
        }
        var textsByNodeId = new Dictionary<int, string>();
        foreach (var n in tasks) {
            if (n.TextIndex != null) {
                textsByNodeId[n.NodeId] = n.TextIndex;
            }
        }
        foreach (var task in tasks) {
            string? text = null;
            textsByNodeId.TryGetValue(task.NodeId, out text);
            float[]? vector = null;
            vectorsByNodeId.TryGetValue(task.NodeId, out vector);
            if (text != null && vector != null) {
                t.UpdateProperties(task.NodeId,
                    [NodeConstants.SystemTextIndexPropertyId, NodeConstants.SystemVectorIndexPropertyId],
                    [text, vector]);
            } else if (text != null) {
                t.UpdateProperty(task.NodeId, NodeConstants.SystemTextIndexPropertyId, text);
            } else if (vector != null) {
                t.UpdateProperty(task.NodeId, NodeConstants.SystemVectorIndexPropertyId, vector);
            } else {
                // should never happen, but just in case
                throw new Exception("Internal error");
            }
        }
    }
    public override bool DeleteOnSuccess => true;
    public override TimeSpan GetMaximumAgeInPersistedQueue() => TimeSpan.FromHours(1);
    public override bool RestartIfAbortedDuringShutdown { get; set; } = true;
    public override TextOrSemanticIndexTask TaskFromBytes(byte[] bytes) {
        using var ms = new MemoryStream(bytes);
        using var reader = new BinaryReader(ms);
        var nodeId = reader.ReadInt32();
        var textIndex = ms.Position < ms.Length ? reader.ReadString() : null;
        return new TextOrSemanticIndexTask(nodeId, textIndex);
    }
    public override byte[] TaskToBytes(TextOrSemanticIndexTask task) {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(task.NodeId);
        if (task.TextIndex != null) writer.Write(task.TextIndex);
        return ms.ToArray();
    }
}
