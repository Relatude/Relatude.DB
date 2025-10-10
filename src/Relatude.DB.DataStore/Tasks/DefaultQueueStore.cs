using System.IO.Compression;
using Relatude.DB.Common;
using Relatude.DB.IO;

namespace Relatude.DB.Tasks;
// Not threadsafe, handled by outer TaskQueue
public class DefaultQueueStore : IQueueStore {
    IIOProvider? _io;
    readonly bool _persistToDisk;
    IAppendStream? _stream;
    string? _fileKey;
    IAppendStream stream {
        get {
            if (_stream != null) return _stream;
            if (!_persistToDisk) throw new InvalidOperationException("Queue store is not configured to persist to disk.");
            if (_io == null || _fileKey == null) throw new InvalidOperationException("IOProvider or fileKey not set.");
            _stream = _io.OpenAppend(_fileKey);
            return _stream;
        }
    }
    Dictionary<Guid, IBatch> _batchesById = [];
    readonly Dictionary<string, ITaskRunner> _runners;
    bool _unflushed = true;
    public DefaultQueueStore(Dictionary<string, ITaskRunner> runners, IIOProvider? io = null, string? fileKey = null) {
        _io = io;
        _fileKey = fileKey;
        _runners = runners;
        _persistToDisk = _io != null && !string.IsNullOrEmpty(_fileKey);
        ReOpen();
    }
    static Guid _marker = Guid.Parse("a833eb7b-9cfb-4625-a7f3-d431e063fdc6");
    static Dictionary<Guid, IBatch> loadFromDisk(Dictionary<string, ITaskRunner> runners, IIOProvider io, string fileKey) {
        Dictionary<Guid, IBatch> batchesById = [];
        if (io.DoesNotExistOrIsEmpty(fileKey)) return batchesById;
        using var reader = io.OpenRead(fileKey, 0);
        while (reader.Position < reader.Length) {
            bool found = reader.MoveToNextValidMarker(_marker);
            if (!found) break; // no more markers found
            try {
                var isStateFlag = reader.ReadVerifiedInt(); // 10 = deleted, 20 = not deleted, else throw error!
                if (isStateFlag == 10) { // deleted state
                    var batchId = reader.ReadGuid();
                    if (batchesById.ContainsKey(batchId)) batchesById.Remove(batchId);
                } else if (isStateFlag == 20) { // deleted state
                    var bytesMeta = reader.ReadByteArray();
                    var bytesTasks = reader.ReadByteArray();
                    var meta = BatchMeta.FromBytes(bytesMeta);
                    if (runners.TryGetValue(meta.TaskTypeId, out var runner)) {
                        try {
                            var batch = runner.GetBatchFromMetaAndData(meta, bytesTasks);
                            batchesById[meta.BatchId] = batch;
                        } catch {
                            // If we cannot deserialize the tasks, we ignore this batch
                        }
                    } else {
                        // ignore
                    }
                } else {
                    throw new Exception($"Invalid state flag {isStateFlag} in queue store file {fileKey}. Expected 10 (deleted) or 20 (not deleted).");
                }
            } catch (Exception ex) {
                Console.WriteLine($"Error reading batch from queue store file {fileKey} at position {reader.Position}: {ex.Message}");
                // ignore, move to next marker
            }
        }
        return batchesById;
    }
    void writeBatchToDisk(IBatch batch, ITaskRunner runner) {
        stream.WriteMarker(_marker);
        stream.WriteVerifiedInt(20); // 20 = not deleted state
        var bytesMeta = batch.Meta.ToBytes();
        var bytesTasks = batch.TasksToBytes(runner);
        stream.WriteByteArray(bytesMeta);
        stream.WriteByteArray(bytesTasks);
        _unflushed = true;
    }
    void writeDeleteStateToDisk(Guid taskId) {
        stream.WriteMarker(_marker);
        stream.WriteVerifiedInt(10); // 10 = deleted state
        stream.WriteGuid(taskId);
        _unflushed = true;
    }
    void deleteStream() {
        _stream?.Dispose();
        _stream = null;
        _io?.DeleteIfItExists(_fileKey!);
    }
    public void FlushDiskIfNeeded() {
        if (_stream != null && _unflushed) {
            _stream.Flush();
            _unflushed = false;
        }
    }
    public void Enqueue(IBatch batch, ITaskRunner runner) {
        _batchesById[batch.Meta.BatchId] = batch;
        if (_persistToDisk) writeBatchToDisk(batch, runner);
    }
    public void Delete(Guid[] batchIds) {
        foreach (var batchId in batchIds) {
            if (_batchesById.ContainsKey(batchId)) {
                _batchesById.Remove(batchId);
                if (_persistToDisk) {
                    if (_batchesById.Count == 0) deleteStream();
                    else writeDeleteStateToDisk(batchId);
                }
            }
        }
    }
    public IBatch? DequeueAndSetRunning(Dictionary<string, ITaskRunner> runners) {
        IBatch? task = _batchesById.Values
            .Where(b => b.Meta.State == BatchState.Pending)
            .OrderByDescending(b => b.Meta.Priority)
            .OrderBy(b => b.Meta.CreatedUtc)
            .FirstOrDefault();
        if (task == null) return null; // no pending tasks
        _batchesById[task.Meta.BatchId].Meta.State = BatchState.Running;
        if (_persistToDisk) writeBatchToDisk(task, runners[task.Meta.TaskTypeId]);
        return task;
    }
    public int CountBatch(BatchState state) => _batchesById.Values.Count(b => b.Meta.State == state);
    public int CountTasks(BatchState state) => _batchesById.Values.Where(b => b.Meta.State == state).Sum(b => b.TaskCount);
    public bool AnyPendingOrRunning() => _batchesById.Values.Any(b => b.Meta.State == BatchState.Pending || b.Meta.State == BatchState.Running);
    public BatchMetaWithCount[] GetBatchInfo(BatchState[] states, string[] typeIds, string[] jobIds, int page, int pageSize, out int totalCount) {
        totalCount = 0;
        var batches = _batchesById.Values
            .Where(b =>
                (states.Length == 0 || states.Contains(b.Meta.State)) &&
                (typeIds.Length == 0 || typeIds.Contains(b.Meta.TaskTypeId)) &&
                (jobIds.Length == 0 || jobIds.Contains(b.Meta.JobId))
                )
            .ToArray();
        totalCount = batches.Count();
        return batches.Select(b => new BatchMetaWithCount(b.Meta, b.TaskCount))
            .OrderBy(b => b.CreatedUtc).Skip(page * pageSize).Take(pageSize).ToArray();
    }
    public void Set(Guid[] batchIds, BatchState state) {
        foreach (var batchId in batchIds) {
            if (_batchesById.TryGetValue(batchId, out var batch)) {
                batch.Meta.State = state;
                if (_persistToDisk) writeBatchToDisk(batch, _runners[batch.Meta.TaskTypeId]);
            }
        }
    }
    public void Set(string jobId, BatchState state) {
        foreach (var batch in _batchesById.Values) {
            if (batch.Meta.JobId == jobId) {
                batch.Meta.State = state;
                if (_persistToDisk) writeBatchToDisk(batch, _runners[batch.Meta.TaskTypeId]);
            }
        }
    }
    public void Set(Guid batchId, Exception error) {
        var batch = _batchesById[batchId];
        batch.Meta.State = BatchState.Failed;
        batch.Meta.ErrorType = error.GetType().FullName ?? "Unknown";
        batch.Meta.ErrorMessage = error.Message;
        if (_persistToDisk) writeBatchToDisk(batch, _runners[batch.Meta.TaskTypeId]);
    }
    public KeyValuePair<BatchState, int>[] BatchCountsPerState() {
        var counts = new Dictionary<BatchState, int>();
        foreach (var batch in _batchesById.Values) {
            if (!counts.ContainsKey(batch.Meta.State)) counts[batch.Meta.State] = 0;
            counts[batch.Meta.State]++;
        }
        return [.. counts];
    }
    public KeyValuePair<BatchState, int>[] TaskCountsPerState() {
        var counts = new Dictionary<BatchState, int>();
        foreach (var batch in _batchesById.Values) {
            if (!counts.ContainsKey(batch.Meta.State)) counts[batch.Meta.State] = 0;
            counts[batch.Meta.State] += batch.TaskCount;
        }
        return [.. counts];
    }
    public void Delete(BatchState[] states, string[] typeIds) {
        var toDelete = _batchesById.Values
            .Where(b =>
                (states.Length == 0 || states.Contains(b.Meta.State)) &&
                (typeIds.Length == 0 || typeIds.Contains(b.Meta.TaskTypeId)))
            .Select(b => b.Meta.BatchId).ToArray();
        Delete(toDelete);
    }
    public void ReOpen() {
        Dispose();
        if (_persistToDisk) {
            if (_io == null || string.IsNullOrEmpty(_fileKey)) throw new InvalidOperationException("IOProvider or fileKey not set.");
            try {
                _batchesById = loadFromDisk(_runners, _io, _fileKey);
            } catch {
                _io.DeleteIfItExists(_fileKey);
                _batchesById = [];
            }
            if (_batchesById.Count == 0) _io.DeleteIfItExists(_fileKey); // no batches, delete file
        } else {
            _batchesById = [];
        }
    }
    public void Dispose() {
        if (_stream != null) {
            _stream.Dispose();
            _stream = null;
        }
    }
}