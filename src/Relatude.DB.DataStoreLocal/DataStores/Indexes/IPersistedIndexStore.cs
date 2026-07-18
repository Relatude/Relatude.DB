using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes;

public interface IPersistedIndexStore : IDisposable {
    Guid GetWalFileId();
    IValueIndex<T> OpenValueIndex<T>(SetRegister sets, string id, string friendlyName, PropertyType type) where T : notnull;
    IWordIndex OpenWordIndex(SetRegister sets, string id, string friendlyName, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch);
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
    long GetTotalDiskSpace();
    void OptimizeDisk();
    void ReOpen();
    void ResetAll();
}
