using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Indexes.Trie.CharArraySearch.Trie.Node; 
internal class NodeWithValue<T> : NodeBase<T> {
    public T? Value;
    public char[] Tail;
    public override int GetNodeCount() => 1;
    public NodeWithValue(char character, char[] tail) {
        Character= character;
        Tail= tail;
    }
    public NodeWithValue(char[] chars, int shift, T? value) {
        Character = chars[shift];
        Tail = new char[chars.Length - shift - 1];
        Buffer.BlockCopy(chars, 2 * shift + 2, Tail, 0, 2 * Tail.Length);
        Value = value;
    }
    public override string ToString() {
        return (Character + new string(Tail)).ToUpper() + ": " + Value;
    }
    public override void BuildListOfSimilarWords(char[] searchWord, int pos, char[] currentWord, int maxLev, List<SpellHit<T?>> words, Levenshtein lev, int max) {
        if (words.Count >= max) return;
        var fullLength = pos + Tail.Length;
        if (Math.Abs(fullLength - searchWord.Length) > maxLev) return; // no need to check lev.
        var fullWord = new char[fullLength];
        Array.Copy(currentWord, 0, fullWord, 0, pos);
        Array.Copy(Tail, 0, fullWord, pos, Tail.Length);
        var levdist = lev.DistanceFrom(fullWord, fullWord.Length, searchWord.Length);
        if (levdist <= maxLev && levdist > 0) {
            words.Add(new SpellHit<T?>(fullWord, levdist, Value));
        }
    }
    public override void Write(IAppendStream stream) {
        stream.WriteChar(Character);
        stream.WriteCharArray(Tail);
    }
    public static NodeWithValue<T> Read(IReadStream stream) {
        var character = stream.ReadChar();
        var tail = stream.ReadCharArray();
        return new NodeWithValue<T>(character, tail);
    }
}
