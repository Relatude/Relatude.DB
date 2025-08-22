using WAF.Common;
using WAF.DataStores.Indexes.Trie.TrieNet._Ukkonen;

namespace WAF.DataStores.Indexes.Trie.CharArraySearch; 
public class InFixVariations {
    UkkonenTrie<char[]> _infixTrie;
    HashSet<ulong> _words;
    public InFixVariations(int minLength) {
        _infixTrie = new UkkonenTrie<char[]>(minLength);
        _words = new HashSet<ulong>();
    }
    public void Add(string wordString, char[] wordArray) {
        var h = wordString.XXH64Hash();
        if (_words.Contains(h)) return;
        _words.Add(h);
        _infixTrie.Add(wordString, wordArray);
    }
    public IEnumerable<char[]> Retrieve(string word) => _infixTrie.Retrieve(word);
}
