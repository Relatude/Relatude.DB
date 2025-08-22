namespace WAF.DataStores.Indexes.Trie.CharArraySearch.Trie.Node;
internal class NodeWithChildrenAndValue<T> : NodeWithChildren<T> {
    public NodeWithChildrenAndValue() { }
    public NodeWithChildrenAndValue(char character, T? value) : base(character) {
        Value = value;
    }
    public T? Value;
    public override string ToString() {
        return Character.ToString().ToUpper() + "(" + Children.Length + ")" + ": " + Value;
    }
    public override void BuildListOfSimilarWords(char[] searchWord, int pos, char[] currentWord, int maxLev, List<SpellHit<T?>> words, Levenshtein lev, int max) {
        if (words.Count >= max) return;
        var levdist = lev.DistanceFrom(currentWord, pos, searchWord.Length);
        if (levdist <= maxLev && levdist > 0) {
            var fullWord = new char[pos];
            Array.Copy(currentWord, 0, fullWord, 0, pos);
            words.Add(new SpellHit<T?>(fullWord, levdist, Value));
        }
        base.BuildListOfSimilarWords(searchWord, pos, currentWord, maxLev, words, lev, max);
    }
}
