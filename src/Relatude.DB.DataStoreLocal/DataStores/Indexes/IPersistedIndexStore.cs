using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes;

public interface IPersistedIndexStore : IDisposable {
    Guid WalFileId { get; }
    IValueIndex<T> OpenValueIndex<T>(SetRegister sets, string id, string friendlyName, PropertyType type) where T : notnull;
    IWordIndex OpenWordIndex(SetRegister sets, string id, string friendlyName, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch);
    void SetWalFileId(Guid walFileId);
    void UpdateTimestampsDueToHotswap(long timestamp, Guid walFileId);
    static void DeleteFilesInDefaultFolder(string databaseFolderPath, string? filePrefix) {
        var path = Path.Combine(databaseFolderPath, new FileKeyUtility(filePrefix).IndexStoreFolderKey);
        if (Directory.Exists(path)) {
            try {
                Directory.Delete(path, true);
            } catch {
            }
        }
    }
    void StartTransaction();
    void CommitTransaction(long timestamp);
    long GetTotalDiskSpace();
    void OptimizeDisk();
    void ReOpen();
    void ResetAll();
}
