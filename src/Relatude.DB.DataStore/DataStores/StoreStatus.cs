using Relatude.DB.Common;
using Relatude.DB.Serialization;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores;
public class StoreStatus {

    public Dictionary<string, int> TypeCounts { get; set; } = [];
    public Dictionary<string, int> QueuedTaskStateCounts { get; set; } = [];
    public Dictionary<string, int> QueuedTaskStateCountsPersisted { get; set; } = [];
    public Dictionary<string, int> QueuedBatchesStateCounts { get; set; } = [];
    public Dictionary<string, int> QueuedBatchesStateCountsPersisted { get; set; } = [];

    public DateTime Created { get; } = DateTime.UtcNow;
    public bool IsFresh { get; set; }
    public int AgeMs => (int)Math.Round((DateTime.UtcNow - Created).TotalMilliseconds);

    public long UptimeMs { get; set; }
    public long StartUpMs { get; set; }
    public DateTime InitiatedUtc { get; set; }
    public DateTime? LogFirstStateUtc { get; set; } = null;
    public DateTime? LogLastChange { get; set; } = null;

    public long LogTruncatableActions { get; set; }
    public long LogActionsNotItInStatefile { get; set; }
    public long LogTransactionsNotItInStatefile { get; set; }
    public int LogWritesQueuedTransactions { get; set; }
    public int LogWritesQueuedActions { get; set; }
    public string? LogFileKey { get; set; }
    public long LogFileSize { get; set; }
    public long LogStateFileSize { get; set; }

    public long CountActionsSinceClearCache { get; set; }
    public long CountTransactionsSinceClearCache { get; set; }
    public long CountQueriesSinceClearCache { get; set; }
    public long CountNodeGetsSinceClearCache { get; set; }

    public long DatamodelPropertyCount { get; set; }
    public long DatamodelNodeTypeCount { get; set; }
    public long DatamodelRelationCount { get; set; }
    public long DatamodelIndexCount { get; set; }

    public int RelationCount { get; set; }

    public int NodeCount { get; set; }
    public int NodeCacheCount { get; set; }
    public long NodeCacheSize { get; set; }
    public int NodeCacheCountOfUnsaved { get; set; }
    public double NodeCacheSizePercentage { get; set; }
    public long NodeCacheHits { get; set; }
    public long NodeCacheMisses { get; set; }
    public long NodeCacheOverflows { get; set; }

    public int AggregateCacheCount { get; set; }
    public long AggregateCacheHits { get; set; }
    public long AggregateCacheMisses { get; set; }
    public long AggregateCacheOverflows { get; set; }

    public int SetCacheCount { get; set; }
    public long SetCacheSize { get; set; }
    public long SetCacheHits { get; set; }
    public long SetCacheMisses { get; set; }
    public double SetCacheSizePercentage { get; set; }
    public long SetCacheOverflows { get; set; }

    public int QueuedTasksPending { get; set; }
    public int QueuedTasksPendingPersisted { get; set; }

    public int QueuedBatchesPending { get; set; }
    public int QueuedBatchesPendingPersisted { get; set; }

    public long DiskSpacePersistedIndexes { get; set; }
    public long ProcessWorkingMemory { get; set; }

    public static StoreStatus DeSerialize(Stream stream) {
        var json = stream.ReadString();
        return FromJson.StoreInfo(json);
    }
}
