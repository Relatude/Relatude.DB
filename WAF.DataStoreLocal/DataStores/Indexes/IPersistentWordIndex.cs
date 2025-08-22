using WAF.DataStores.Sets;
namespace WAF.DataStores.Indexes;
public interface IPersistentWordIndexFactory {
    IPersistentWordIndex Create(SetRegister sets, IPersistedIndexStore index, string key, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch);
    void DeleteAllFiles();
}
public interface IPersistentWordIndex : IWordIndex {
    void Commit();
    void Close();
    void Open();
    void OptimizeAndMerge();
}
