using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes;
public interface IPersistedIndexStore : IDisposable {
    IValueIndex<T> OpenValueIndex<T>(SetRegister sets, string id, PropertyType type) where T : notnull;
    IWordIndex OpenWordIndex(SetRegister sets, string id, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch);
    Guid LogFileId { get; }
    void FlushAndCommitTimestamp(long timestamp);
    public static void DeleteFilesInDefaultFolder(string databaseFolderPath, string? filePrefix) {
        var path = Path.Combine(databaseFolderPath, new FileKeyUtility(filePrefix).IndexStoreFolderKey);
        if (Directory.Exists(path)) {
            try {
                Directory.Delete(path, true);
            } catch {
            }
        }
    }
    long GetTotalDiskSpace();
    void OptimizeDisk();
    void ReOpen();
}
