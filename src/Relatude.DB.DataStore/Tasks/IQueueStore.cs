using static System.Formats.Asn1.AsnWriter;

namespace Relatude.DB.Tasks;
// Not threadsafe
public interface IQueueStore : IDisposable {
    void Enqueue(IBatch task, ITaskRunner runner);
    IBatch? DequeueAndSetRunning(Dictionary<string, ITaskRunner> runners);
    int CountBatch(BatchState state);
    int CountTasks(BatchState state);
    BatchMetaWithCount[] GetBatchInfo(BatchState[] states, string[] typeIds, string[] jobIds, int page, int pageSize, out int totalCount);
    void Set(Guid[] batchIds, BatchState state);
    void Set(string jobId, BatchState state);
    void Set(Guid batchId, Exception error);
    void Delete(Guid[] batchIds);
    void Delete(BatchState[] states, string[] typeIds);
    void FlushDiskIfNeeded();
    void ReOpen();
}
public static class QueueStoreExtensions {
    static public KeyValuePair<BatchState, int>[] BatchCountsPerState(this IQueueStore q) {
        return [.. Enum.GetValues<BatchState>().Select(state => new KeyValuePair<BatchState, int>(state, q.CountBatch(state))).Where(x => x.Value > 0)];
    }
    static public KeyValuePair<BatchState, int>[] TaskCountsPerState(this IQueueStore q) {
        return [.. Enum.GetValues<BatchState>().Select(state => new KeyValuePair<BatchState, int>(state, q.CountTasks(state))).Where(x => x.Value > 0)];
    }
}