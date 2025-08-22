using System.Text;
namespace WAF.DataStores.Indexes.Trie.CharArraySearch.Trie.Node; 
internal static class TrieNodeHelpers<T> {
    public static NodeWithChildren<T> InsertEdgeNodeAndChangeParentToCorrectNodeType(char[] word, int shift, T? value, NodeWithValue<T> valueNode) {
        NodeWithChildren<T> newNode;
        if (word.Length - shift == 1 && valueNode.Tail.Length == 0) {
            throw new Exception("\"" + new string(word) + "\" already added. ");
        } else if (word.Length - shift == 1) {
            newNode = new NodeWithChildrenAndValue<T>(word[shift], value);
            newNode.Add(valueNode.Tail, 0, valueNode.Value);
        } else if (valueNode.Tail.Length == 0) {
            newNode = new NodeWithChildrenAndValue<T>(valueNode.Character, valueNode.Value);
            newNode.Add(word, shift + 1, value);
        } else {
            newNode = new NodeWithChildren<T>(word[shift]);
            newNode.Add(valueNode.Tail, 0, valueNode.Value);
            newNode.Add(word, shift + 1, value);
        }
        return newNode;
    }
    public static void ForEachNode(NodeBase<T> node, nodeCallback<T> callback) {
        callback(node);
        if (node is NodeWithChildren<T> nc) foreach (var c in nc.Children) ForEachNode(c, callback);
    }
    public static void ForEachWord(NodeBase<T> node, string word, wordCallback callback) {
        if (node is NodeWithValue<T> edge) callback(word + node.Character + new string(edge.Tail));
        else if (node is NodeWithChildrenAndValue<T>) callback(word + node.Character);
        if (node is NodeWithChildren<T> nc) foreach (var c in nc.Children) ForEachWord(c, word + node.Character, callback);
    }
    public static void ForEachWordAndValue(NodeBase<T> node, string word, wordAndValueCallback<T> callback) {
        if (node is NodeWithValue<T> edge) callback(word + node.Character + new string(edge.Tail), edge.Value);
        else if (node is NodeWithChildrenAndValue<T> nodeV) callback(word + node.Character, nodeV.Value);
        if (node is NodeWithChildren<T> nc) foreach (var c in nc.Children) ForEachWordAndValue(c, word + node.Character, callback);
    }
    public static void ForEachValue(NodeBase<T> node, valueCallback<T> callback) {
        if (node is NodeWithValue<T> edge) callback(edge.Value);
        else if (node is NodeWithChildrenAndValue<T> nodeV) callback(nodeV.Value);
        if (node is NodeWithChildren<T> nc) foreach (var c in nc.Children) ForEachValue(c, callback);
    }
    public static void CountNodes(NodeBase<T> node, ref int count) {
        count++;
        if (node is NodeWithChildren<T>) foreach (var c in ((NodeWithChildren<T>)node).Children) CountNodes(c, ref count);
    }
    public static void CountWords(NodeBase<T> node, ref int count) {
        if (node is NodeWithValue<T> || node is NodeWithChildrenAndValue<T>) count++;
        if (node is NodeWithChildren<T>) {
            foreach (var c in ((NodeWithChildren<T>)node).Children) CountWords(c, ref count);
        }
    }
    public static void BuildStringAll(NodeBase<T> node, StringBuilder sb, int level) {
        sb.Append(new string(' ', level * 5));
        sb.Append(node.ToString());
        sb.AppendLine();
        if (node is NodeWithChildren<T>) {
            foreach (var c in ((NodeWithChildren<T>)node).Children) BuildStringAll(c, sb, level + 1);
        }
    }
    public static bool IsWordEqual(char[] w1, char[] w2, int shift2) {
        if (w1.Length != w2.Length - shift2) return false;
        for (int i = 0; i < w1.Length; i++) if (w1[i] != w2[i + shift2]) return false;
        return true;
    }
    public static bool HasWordEqualPrefix(char[] w1, int shift1, char[] w2) {
        if (w1.Length - shift1 > w2.Length) return false;
        for (int i = 0; i < w1.Length - shift1; i++) if (w1[i + shift1] != w2[i]) return false;
        return true;
    }
    public static void AddAllChildValues(NodeWithChildren<T> node, List<T?> values, int maxHits) {
        if (values.Count >= maxHits) return;
        foreach (var c in node.Children) {
            if (c is NodeWithValue<T> cEdge) {
                values.Add(cEdge.Value);
            } else if (c is NodeWithChildrenAndValue<T> cNodeV) {
                values.Add(cNodeV.Value);
                AddAllChildValues(cNodeV, values, maxHits);
            } else {
                AddAllChildValues((NodeWithChildren<T>)c, values, maxHits);
            }
        }
    }
    public static void SearchPrefix(NodeWithChildren<T> node, char[] search, int shift, List<T?> values, int maxHits) {
        if (values.Count >= maxHits) return;
        if (node.TryGetNotRecursive(search[shift], out var c, out _)) {
            if (c is NodeWithValue<T> cEdge) {
                if (HasWordEqualPrefix(search, shift + 1, cEdge.Tail)) {
                    values.Add(cEdge.Value);
                }
            }
            if (c is NodeWithChildrenAndValue<T> cNodeV && search.Length - shift == 1) {
                values.Add(cNodeV.Value);
            }
            if (c is NodeWithChildren<T> cNode) {
                if (search.Length - shift > 1) {
                    SearchPrefix(cNode, search, shift + 1, values, maxHits);
                } else {
                    AddAllChildValues(cNode, values, maxHits);
                }
            }
        }
    }
    static void AddAllChildValuesWithWords(NodeWithChildren<T> node, char[] search, string path, int shift, List<KeyValuePair<string, T?>> values) {
        foreach (var c in node.Children) {
            if (c is NodeWithValue<T> cEdge) {
                var word = path + cEdge.Character + new string(cEdge.Tail);
                values.Add(new (word, cEdge.Value));
            } else if (c is NodeWithChildrenAndValue<T> cNodeV) {
                var word = path + cNodeV.Character;
                values.Add(new (word, cNodeV.Value));
                AddAllChildValuesWithWords(cNodeV, search, path + cNodeV.Character, shift + 1, values);
            } else {
                var cNode = (NodeWithChildren<T>)c;
                AddAllChildValuesWithWords(cNode, search, path + cNode.Character, shift + 1, values);
            }
        }
    }
    public static void SearchPrefixWithWord(NodeWithChildren<T> node, char[] search, string path, int shift, List<KeyValuePair<string, T?>> values) {
        if (node.TryGetNotRecursive(search[shift], out var c, out _)) {
            if (c is NodeWithValue<T> cEdge && HasWordEqualPrefix(search, shift + 1, cEdge.Tail)) {
                var word = path + cEdge.Character + new string(cEdge.Tail);
                values.Add(new (word, cEdge.Value));
            }
            if (c is NodeWithChildrenAndValue<T> cNodeV && search.Length - shift == 1) {
                var word = path + cNodeV.Character;
                values.Add(new (word, cNodeV.Value));
            }
            if (c is NodeWithChildren<T> cNode) {
                if (search.Length - shift > 1) {
                    SearchPrefixWithWord(cNode, search, path + cNode.Character, shift + 1, values);
                } else {
                    AddAllChildValuesWithWords(cNode, search, path + cNode.Character, shift + 1, values);
                }
            }
        }
    }
    public static byte GetTrieNodeTypeId(NodeBase<T> node) {
        if (node is NodeWithValue<T>) return 0;
        if (node is NodeWithChildrenAndValue<T>) return 1;
        return 2;
    }
}
