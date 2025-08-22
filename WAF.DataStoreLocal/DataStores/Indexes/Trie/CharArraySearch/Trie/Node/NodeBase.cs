using WAF.IO;
namespace WAF.DataStores.Indexes.Trie.CharArraySearch.Trie.Node; 
internal abstract class NodeBase<T> {
    public char Character;
    public abstract int GetNodeCount();
    public abstract void BuildListOfSimilarWords(char[] searchWord, int pos, char[] currentWord, int maxLev, List<SpellHit<T?>> words, Levenshtein lev, int max);
    public abstract void Write(IAppendStream stream);
}
