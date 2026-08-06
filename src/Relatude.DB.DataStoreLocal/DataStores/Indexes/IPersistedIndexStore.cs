using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes;

public interface IPersistedIndexStore : IDisposable {
    Guid GetWalFileId();
    IValueIndex<T> OpenValueIndex<T>(SetRegister sets, string id, string friendlyName, PropertyType type) where T : notnull;
    IWordIndex OpenWordIndex(SetRegister sets, string id, string friendlyName, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch);
    IStringArrayIndex StringArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type);
    IGuidArrayIndex GuidArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type);
    IIntArrayIndex IntArrayIndex(SetRegister sets, string id, string friendlyName, PropertyType type);
    void SetWalFileId(Guid walFileId);
    void SetWalFileIdAndTimestamp(long timestamp, Guid walFileId);
    static void DeleteFilesInDefaultFolder(string databaseFolderPath, string? filePrefix) {
        var path = Path.Combine(databaseFolderPath, new FileKeyUtility(filePrefix).IndexStoreFolderKey);
        if (Directory.Exists(path)) {
            try {
                Directory.Delete(path, true);
            } catch {
            }
        }
    }
    /// <summary>
    /// Durably deletes every persisted index that has not been opened in this session, data and
    /// definition included; open indexes are untouched. Call only after every index in the current
    /// schema has been opened: anything still unopened is then an index that has left the schema,
    /// and deleting it ensures a later re-add starts fresh (timestamp 0, forcing a rebuild)
    /// instead of resurrecting stale data that claims to be current.
    /// Only allowed outside a transaction.
    /// </summary>
    void DeleteUnopenedIndexes();
    void BeginTransaction();
    void RollbackTransaction();
    void CleanUpOnUnknownTransactionError();
    void CommitTransaction(long timestamp);
    /// <summary>
    /// Durably persists every transaction committed so far. Backends that defer durability
    /// (see <see cref="PersistedIndexStoreBase.CommitTransactionCore"/>) write their durable
    /// checkpoint here; backends that are durable per commit treat this as a no-op.
    /// The data store calls this right after a successful WAL flush, under the store write lock,
    /// so the durable indexes can never contain transactions the durable log is missing.
    /// Only allowed outside a transaction.
    /// </summary>
    void MakeDurable();
    long GetTimestamp();
    long GetTotalDiskSpace();
    void OptimizeDisk();
    void SaveIndexCaches(bool force);
    void ResetAll();
    void ResetIndexCaches();
}
