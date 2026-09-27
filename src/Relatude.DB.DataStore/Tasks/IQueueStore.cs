namespace Relatude.DB.Tasks;
// Not threadsafe
public interface IQueueStore : IDisposable {
    void Enqueue(IBatch task, ITaskRunner runner);
    IBatch? DequeueAndSetRunning(Dictionary<string, ITaskRunner> runners);
    /// <summary>
    /// The pending batch that would be handed out next - highest priority, then oldest - among those
    /// that can start now: not of a type in <paramref name="excludedTypeIds"/> (types already running
    /// as many batches as they may), and accepted by <paramref name="accept"/>, which sees the whole
    /// batch and is asked in queue order until one passes. The batch is set to running before it is
    /// returned. Null when nothing can start now, even though batches may be waiting.
    /// <para>The default serves a store that only knows the plain dequeue: it takes the next batch and,
    /// when that one cannot start, puts it back and hands out nothing this time - so a store without
    /// its own version never starts a batch past one that has to wait.</para>
    /// </summary>
    IBatch? DequeueAndSetRunning(Dictionary<string, ITaskRunner> runners, IReadOnlySet<string> excludedTypeIds, Func<IBatch, bool>? accept) {
        var batch = DequeueAndSetRunning(runners);
        if (batch == null) return null;
        if (!excludedTypeIds.Contains(batch.Meta.TaskTypeId) && (accept == null || accept(batch))) return batch;
        Set([batch.Meta.BatchId], BatchState.Pending);
        return null;
    }
    bool AnyPendingOrRunning();
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
