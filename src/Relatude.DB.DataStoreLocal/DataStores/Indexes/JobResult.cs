using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes {
    public class JobResult(int count, int total, string description) {
        public int Dequed => count;
        public int TotalInQueue => total;
        public string Description => description;
    }
    //public abstract class IndexBase : IDisposable, IIndex {
    //    /// <summary>
    //    /// This is the base class for all indexes.
    //    /// As a general rule, indexing a value cannot already be indexed.
    //    /// To update an index, first deindex the old value and then index the new value.
    //    /// It must ( and will ) be deindexed with the exact value if was originally indexed with.
    //    /// </summary>
    //    /// <param name="uniqueKey">An ID that is unique for the datamodel but is the same on each startup</param>
    //    protected IndexBase(string uniqueKey) {
    //        UniqueKey = uniqueKey;
    //    }
    //    public string UniqueKey { get; private set; }
    //    public abstract void SaveState(IAppendStream stream); // option to save data to stream for next db open.
    //    public abstract void ReadState(IReadStream stream); // option to read data from data stream on db open
    //    public abstract void IndexValue(int id, object value);
    //    public abstract void DeIndexValue(int id, object value);
    //    public abstract void CompressMemory();
    //    public abstract void ClearCache();
    //    public virtual int GetQueuedTaskCount() => 0;
    //    public virtual Task<JobResult> DequeueTasks() => Task.FromResult(new JobResult(0, 0, string.Empty));
    //    public abstract void Dispose();
    //}
}