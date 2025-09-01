using Relatude.DB.DataStores.Indexes.Trie.CharArraySearch.Trie.Node;
using Relatude.DB.IO;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Relatude.DB.Common;
namespace Relatude.DB.DataStores.Indexes.Trie.CharArraySearch.Trie {
    // My aim has been a search index class with max search speed and minimum mem use, while also providing fast prefix and fuzzy search
    // Mem is saved by using a speparate class for each node type that only stores min needed variables
    // Speed by using char arrays and an efficient Levenstein function that only looks at relevant parts of char array, and without allocation ( new class )
    // Decided to use arrays for node children ( not HashSets or Lists), as number of children should be relatively low (a charset is relatively small)
    // Possible improvements to look at:
    // - investigate if using plain strings actually is slower as char arrays ads complexity
    // - introduce interfaces to reduce amount of casts and cleaner code ( INodeWithChildren, INodeWithValue )
    // - introduce infix search, suffix trees, stemming etc. -> a lot more work could be done here
    // - investigate better result object utilizing "yield" and options where the load of the entiere resultset is delayed/avoided
    // - introduce lazy read/write to disk to reduce mem footprint
    internal class SpellHit<T> {
        internal SpellHit(char[] word, int levDist, T? value) {
            Word = word;
            LevDist = levDist;
            Value = value;
        }
        public char[] Word;
        public int LevDist;
        public T? Value;
    }
    internal struct WordValuePair<T> {
        public WordValuePair(string word, T value) {
            Word = word;
            Value = value;
        }
        public string Word;
        public T Value;
    }
    internal delegate void nodeCallback<T>(NodeBase<T> node);
    internal delegate void wordCallback(string word);
    internal delegate void wordAndValueCallback<T>(string word, T? value);
    internal delegate void valueCallback<T>(T? value);
    internal class ValueNodeRef<T> {
        readonly NodeBase<T> _node;
        internal ValueNodeRef(NodeBase<T> node) {
            _node = node;
        }
        public T? Value {
            get {
                if (_node is NodeWithValue<T> cEdge) return cEdge.Value;
                if (_node is NodeWithChildrenAndValue<T> cNodeV) return cNodeV.Value;
                throw new Exception("Not a value node. ");
            }
            set {
                if (_node is NodeWithValue<T> cEdge) {
                    cEdge.Value = value;
                } else if (_node is NodeWithChildrenAndValue<T> cNodeV) {
                    cNodeV.Value = value;
                } else {
                    throw new Exception("Not a value node. ");
                }
            }
        }
    }
    internal class CharArrayTrie<T> {
        NodeWithChildren<T> _root = new NodeWithChildren<T>();
        public void Add(char[] word, T value) => _root.Add(word, 0, value);
        public void Add(string word, T value) => Add(word.ToCharArray(), value);
        public void Update(char[] word, T? value) => _root.Update(word, value);
        public void Update(string word, T? value) => Update(word.ToCharArray(), value);
        public void Remove(char[] word) {
            _root.Remove(word, 0, _root, -1, null);
        }
        public void Remove(string word) {
            _root.Remove(word.ToCharArray(), 0, _root, -1, null);
        }
        internal int CountNodes() {
            int count = 0;
            foreach (var c in _root.Children) TrieNodeHelpers<T>.CountNodes(c, ref count);
            return count;
        }
        public int CountWords() {
            int count = 0;
            foreach (var c in _root.Children) TrieNodeHelpers<T>.CountWords(c, ref count);
            return count;
        }
        internal void ForEachNode(nodeCallback<T> callback) {
            foreach (var c in _root.Children) TrieNodeHelpers<T>.ForEachNode(c, callback);
        }
        public void ResetAllValues(T? value) {
            ForEachNode(node => {
                if (node is NodeWithValue<T> edge) edge.Value = value;
                else if (node is NodeWithChildrenAndValue<T> nodeV) nodeV.Value = value;
            });
        }
        public void Write(IAppendStream stream) {
            _root.Write(stream);
        }
        public void Read(IReadStream stream) {
            _root = NodeWithChildren<T>.Read(stream, false);
        }
        public void ForEachWord(wordCallback callback) {
            foreach (var c in _root.Children) TrieNodeHelpers<T>.ForEachWord(c, string.Empty, callback);
        }
        public void ForEachWordAndValue(wordAndValueCallback<T> callback) {
            foreach (var c in _root.Children) TrieNodeHelpers<T>.ForEachWordAndValue(c, string.Empty, callback);
        }
        public void ForEachValue(valueCallback<T> callback) {
            foreach (var c in _root.Children) TrieNodeHelpers<T>.ForEachValue(c, callback);
        }
        public virtual T? Get(char[] word) => _root.Get(word, 0);
        public virtual List<T?> SearchExact(char[] word) {
            var result = new List<T?>();
            if (_root.TryGet(word, 0, out var value)) result.Add(value);
            return result;
        }
        public virtual List<T?> SearchPrefix(char[] prefix, int maxHits) => _root.SearchPrefix(prefix, maxHits);
        protected virtual List<KeyValuePair<string, T?>> searchPrefixWithWord(char[] prefix) => _root.SearchPrefixWithWord(prefix);
        //public virtual List<T> SearchSimilar(string prefix) => throw new NotImplementedException();  // to be done, follow same logic as in "Suggest"
        public virtual List<SpellHit<T?>> Suggest(char[] word, int max) {
            DefaultLevenshtein.GetDefaultSearchDistance(word.Length, out var distance1, out var distance2);
            var r = Suggest(word, distance1, max);
            if (r.Count > 2) return r;
            if (distance1 == distance2) return r; // no need to search for distance2 if distance1 and distance2 are the same
            return Suggest(word, distance2, max);
        }
        public List<SpellHit<T?>> Suggest(char[] word, int maxLevenshteinDist, int max) {
            var words = new List<SpellHit<T?>>();
            var buffer = new char[word.Length + maxLevenshteinDist + 1];
            var lev = new Levenshtein(word);
            foreach (var child in _root.Children) {
                buffer[0] = child.Character;
                child.BuildListOfSimilarWords(word, 1, buffer, maxLevenshteinDist, words, lev, max);
            }
            return words;
        }
        public virtual bool TryGet(char[] word, [MaybeNullWhen(false)] out T value) => _root.TryGet(word, 0, out value);
        public virtual bool TryGetValueNodeRef(char[] word, [MaybeNullWhen(false)] out ValueNodeRef<T> valueNode) {
            if (_root.TryGetValueNode(word, 0, out var node)) {
                valueNode = new ValueNodeRef<T>(node);
                return true;
            } else {
                valueNode = null;
                return false;
            }
        }
        public bool Contains(char[] word) => _root.Contains(word, 0);
        public string ToStringAll() {
            StringBuilder sb = new StringBuilder();
            foreach (var c in _root.Children) TrieNodeHelpers<T>.BuildStringAll(c, sb, 0);
            return sb.ToString();
        }
        public virtual void ClearCache() {
        }
    }
}