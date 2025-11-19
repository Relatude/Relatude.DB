using Relatude.DB.DataStores.Sets;
namespace Relatude.DB.DataStores.Indexes;
public interface IPersistentWordIndexFactory {
    IPersistentWordIndex Create(SetRegister sets, IPersistedIndexStore index, string key, string friendlyName, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch);
    void DeleteAllFiles();
}
public interface IPersistentWordIndex : IWordIndex {
    void Commit();
    void Close();
    void Open();
    void OptimizeAndMerge();
}
