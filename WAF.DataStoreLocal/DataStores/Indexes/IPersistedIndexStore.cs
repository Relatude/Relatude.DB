using WAF.Datamodels.Properties;
using WAF.DataStores.Indexes;
using WAF.DataStores.Sets;
using WAF.IO;

namespace WAF.DataStores.Indexes;
public interface IPersistedIndexStore : IDisposable {
    IValueIndex<T> OpenValueIndex<T>(SetRegister sets, string id, PropertyType type) where T : notnull;
    IWordIndex OpenWordIndex(SetRegister sets, string id, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch);
    long Timestamp { get; }
    Guid LogFileId { get; }
    Guid ModelHash { get; }
    void Commit(long timestamp);
    void Reset(Guid logFileId, Guid modelHash);
    public static void DeleteFilesInDefaultFolder(string databaseFolderPath, string? filePrefix) {
        var path = Path.Combine(databaseFolderPath, new FileKeyUtility(filePrefix).IndexStoreFolderKey);
        if (Directory.Exists(path)) {
            try {
                Directory.Delete(path, true);
            } catch {
            }
        }
    }
    void OptimizeDisk();
    void ReOpen();
}
