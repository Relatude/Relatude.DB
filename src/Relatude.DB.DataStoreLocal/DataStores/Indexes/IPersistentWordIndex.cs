using Relatude.DB.DataStores.Sets;
namespace Relatude.DB.DataStores.Indexes;
public interface IPersistentWordIndexFactory {
    IPersistentWordIndex Create(SetRegister sets, IPersistedIndexStore index, string key, string friendlyName, int minWordLength, int maxWordLength, bool prefixSearch, bool infixSearch);
    void DeleteAllFiles();
    /// <summary>
    /// Deletes the files of every word index whose key is not among <paramref name="openKeys"/>
    /// (the keys of the word indexes opened in this session). Lets the index store drop word
    /// indexes that have left the schema, so a later re-add starts with a fresh, empty index.
    /// </summary>
    void DeleteUnopenedFiles(IEnumerable<string> openKeys);
}
public interface IPersistentWordIndex : IWordIndex, IPersistedIndex {
    void Commit();
    void Close();
    void Open();
    void OptimizeAndMerge();
}
