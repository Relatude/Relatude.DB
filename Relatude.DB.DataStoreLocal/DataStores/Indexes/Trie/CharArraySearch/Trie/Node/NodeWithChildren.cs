using Relatude.DB.Common;
using Relatude.DB.IO;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.DataStores.Indexes.Trie.CharArraySearch.Trie.Node; 
internal class NodeWithChildren<T> : NodeBase<T> {
    public NodeBase<T>[] Children = new NodeBase<T>[0];
    public NodeWithChildren() { } // root
    public NodeWithChildren(char character) { Character = character; }
    public override void BuildListOfSimilarWords(char[] searchWord, int pos, char[] currentWord, int maxLev, List<SpellHit<T?>> words, Levenshtein lev, int max) {
        foreach (var child in Children) {
            currentWord[pos] = child.Character;
            if (pos < searchWord.Length && searchWord[pos] == child.Character) {
                // if next char match, follow branch... ( sometimes not correct due to insertion diff, but most times it will result in fewer lev calcs,  it can cause extra call)
                child.BuildListOfSimilarWords(searchWord, pos + 1, currentWord, maxLev, words, lev, max);
            } else {
                //subtracted one as sometimes this intermetiate lev calculation will calc 1 extra for changes for char omition
                //this is an approximation, a better algo could be made here
                //if (lev.DistanceFrom(currentWord, pos + 1, Math.Min(pos + 1, searchWord.Length)) - 1 <= maxLev)
                if (lev.DistanceFrom(currentWord, pos + 1, Math.Min(pos + 1, searchWord.Length)) <= maxLev)
                    child.BuildListOfSimilarWords(searchWord, pos + 1, currentWord, maxLev, words, lev, max);
            }
        }
    }
    public override int GetNodeCount() {
        return 1 + Children.Sum(i => i.GetNodeCount());
    }
    public bool TryGetNotRecursive(char c, [MaybeNullWhen(false)] out NodeBase<T> node, out int index) {
        for (index = 0; index < Children.Length; index++) {
            node = Children[index];
            if (node.Character == c) return true;
        }
        node = default;
        return false;
    }
    public bool TryGetNotRecursive(char c, [MaybeNullWhen(false)] out NodeBase<T> node) {
        foreach (var n in Children) {
            if (n.Character == c) {
                node = n;
                return true;
            }
        }
        node = default;
        return false;
    }
    public void Remove(char[] word, int shift, NodeWithChildren<T> firstParentToKeep, int indexOfChildToRemove, NodeWithChildren<T>? parentOfFirstParentToKeep) {
        if (TryGetNotRecursive(word[shift], out var node, out var index)) {
            if (indexOfChildToRemove == -1) indexOfChildToRemove = index;
            if (node is NodeWithChildren<T> cNode) {
                if (word.Length - shift == 1) { // is match, 
                    if (cNode is NodeWithChildrenAndValue<T>) { // transform to node without value
                        var newNode = new NodeWithChildren<T>(cNode.Character);
                        newNode.Children = cNode.Children;
                        Children[index] = newNode;
                        // later improvement: Should look into converting all branches with sub nodes and not using edges with tail
                        return; // done!
                    } else {
                        throw new Exception("Unknow node: \"" + new string(word) + "\""); // reached end of word, but still no match...
                    }
                } else { // traverse down branch...
                    if (cNode is NodeWithChildrenAndValue<T> || cNode.Children.Length > 1) {
                        firstParentToKeep = cNode;
                        parentOfFirstParentToKeep = this;
                        indexOfChildToRemove = -1;
                    }
                    cNode.Remove(word, shift + 1, firstParentToKeep, indexOfChildToRemove, parentOfFirstParentToKeep);
                }
            } else if (node is NodeWithValue<T> nodeWV) {
                if (word.Length - shift - nodeWV.Tail.Length > 1) throw new Exception("Unknow node: \"" + new string(word) + "\""); // reached end of branch, but word is longer...
                if (firstParentToKeep.Children.Length == 1) { // means branch will be cut off from parent, so convert parent to edge
                    if (firstParentToKeep is NodeWithChildrenAndValue<T> v) {
                        var newEdge = new NodeWithValue<T>(new char[] { v.Character }, 0, v.Value);
                        if (parentOfFirstParentToKeep != null) {
                            parentOfFirstParentToKeep.TryGetNotRecursive(firstParentToKeep.Character, out _, out var index2);
                            parentOfFirstParentToKeep.Children[index2] = newEdge; // replace it
                        }
                    } else if (firstParentToKeep.Character == 0) { // root node
                        firstParentToKeep.Children = firstParentToKeep.Children.CopyAndRemoveAt(indexOfChildToRemove);
                    } else {
                        throw new Exception("Error in three structure. Internal Error. Expected Node With Value");
                    }
                } else {
                    firstParentToKeep.Children = firstParentToKeep.Children.CopyAndRemoveAt(indexOfChildToRemove);
                }
            } else {
                throw new Exception("Error in tree. Trying to remove node that does not exists. ");
            }
        } else {
            throw new Exception("Unknow node: \"" + new string(word) + "\"");
        }
    }
    public void Add(char[] word, int shift, T? value) {
        if (TryGetNotRecursive(word[shift], out var node, out var index)) {
            if (node is NodeWithChildren<T> cNode) {
                if (word.Length - shift == 1) {
                    if (node is NodeWithChildrenAndValue<T>) throw new Exception("\"" + new string(word) + "\" already added. ");
                    var newNode = new NodeWithChildrenAndValue<T>(cNode.Character, value);
                    newNode.Children = cNode.Children;
                    Children[index] = newNode;
                } else {
                    cNode.Add(word, shift + 1, value);
                }
            } else {
                Children[index] = TrieNodeHelpers<T>.InsertEdgeNodeAndChangeParentToCorrectNodeType(word, shift, value, (NodeWithValue<T>)node);
            }
        } else {
            node = new NodeWithValue<T>(word, shift, value);
            Array.Resize(ref Children, Children.Length + 1);
            Children[^1] = node;
        }
    }
    public void Update(char[] word, T? value) {
        var node = GetNode(word, 0);
        if (node is NodeWithValue<T> n) n.Value = value;
        else if (node is NodeWithChildrenAndValue<T> n2) n2.Value = value;
        else throw new Exception("Unknown node. ");
    }
    public bool Contains(char[] word, int shift) {
        if (TryGetNotRecursive(word[shift], out var node)) {
            if (node is NodeWithValue<T> cEdge) {
                if (TrieNodeHelpers<T>.IsWordEqual(cEdge.Tail, word, shift + 1)) return true;
            } else if (node is NodeWithChildrenAndValue<T> eNodeV) {
                if (word.Length - shift == 1) return true;
                return eNodeV.Contains(word, shift + 1);
            } else if (node is NodeWithChildren<T> cNode) {
                if (word.Length - shift > 1) return cNode.Contains(word, shift + 1);
            }
        }
        return false;
    }
    public NodeBase<T> GetNode(char[] word, int pos) {
        if (TryGetNotRecursive(word[pos], out var node)) {
            if (node is NodeWithValue<T> cEdge) {
                if (TrieNodeHelpers<T>.IsWordEqual(cEdge.Tail, word, pos + 1)) return cEdge;
            } else if (node is NodeWithChildrenAndValue<T> cNodeV) {
                if (word.Length - pos == 1) return cNodeV;
                return cNodeV.GetNode(word, pos + 1);
            } else {
                if (word.Length - pos > 1) return ((NodeWithChildren<T>)node).GetNode(word, pos + 1);
            }
        }
        throw new Exception("\"" + new string(word) + "\" is unknown. ");
    }
    public T? Get(char[] word, int pos) {
        if (TryGetNotRecursive(word[pos], out var node)) {
            if (node is NodeWithValue<T> cEdge) {
                if (TrieNodeHelpers<T>.IsWordEqual(cEdge.Tail, word, pos + 1)) return cEdge.Value;
            } else if (node is NodeWithChildrenAndValue<T> cNodeV) {
                if (word.Length - pos == 1) return cNodeV.Value;
                return cNodeV.Get(word, pos + 1);
            } else {
                if (word.Length - pos > 1) return ((NodeWithChildren<T>) node).Get(word, pos + 1);
            }
        }
        throw new Exception("\"" + new string(word) + "\" is unknown. ");
    }
    public bool TryGet(char[] word, int pos, out T? value) {
        if (TryGetNotRecursive(word[pos], out var node)) {
            if (node is NodeWithValue<T> cEdge) {
                if (TrieNodeHelpers<T>.IsWordEqual(cEdge.Tail, word, pos + 1)) { value = cEdge.Value; return true; }
            } else if (node is NodeWithChildrenAndValue<T> cNodeV) {
                if (word.Length - pos == 1) { value = cNodeV.Value; return true; }
                return cNodeV.TryGet(word, pos + 1, out value);
            } else {
                if (word.Length - pos > 1) return ((NodeWithChildren<T>)node).TryGet(word, pos + 1, out value);
            }
        }
        value = default;
        return false;
    }
    public bool TryGetValueNode(char[] word, int pos, [MaybeNullWhen(false)] out NodeBase<T> valueNode) {
        if (TryGetNotRecursive(word[pos], out var node)) {
            if (node is NodeWithValue<T> cEdge) {
                if (TrieNodeHelpers<T>.IsWordEqual(cEdge.Tail, word, pos + 1)) { valueNode = cEdge; return true; }
            } else if (node is NodeWithChildrenAndValue<T> cNodeV) {
                if (word.Length - pos == 1) { valueNode = cNodeV; return true; }
                return cNodeV.TryGetValueNode(word, pos + 1, out valueNode);
            } else {
                if (word.Length - pos > 1) return ((NodeWithChildren<T>)node).TryGetValueNode(word, pos + 1, out valueNode);
            }
        }
        valueNode = null;
        return false;
    }
    public List<T?> SearchPrefix(char[] word, int maxHits) {
        var list = new List<T?>();
        TrieNodeHelpers<T>.SearchPrefix(this, word, 0, list, maxHits);
        return list;
    }
    public List<KeyValuePair<string, T?>> SearchPrefixWithWord(char[] word) {
        var list = new List<KeyValuePair<string, T?>>();
        TrieNodeHelpers<T>.SearchPrefixWithWord(this, word, string.Empty, 0, list);
        return list;
    }
    public override string ToString() {
        return Character + "(" + Children.Length + ")";
    }
    public override void Write(IAppendStream stream) {
        stream.WriteChar(Character);
        stream.WriteVerifiedInt(Children.Length);
        foreach (var child in Children) {
            stream.WriteOneByte(TrieNodeHelpers<T>.GetTrieNodeTypeId(child));
            child.Write(stream);
        }
    }
    public static NodeWithChildren<T> Read(IReadStream stream, bool withValue) {
        NodeWithChildren<T> node;
        if (withValue) {
            node = new NodeWithChildrenAndValue<T>();
        } else {
            node = new NodeWithChildren<T>();
        }
        node.Character = stream.ReadChar();
        var count = stream.ReadVerifiedInt();
        node.Children = new NodeBase<T>[count];
        for (int i = 0; i < count; i++) {
            var typeId = stream.ReadOneByte();
            NodeBase<T> child;
            if (typeId == 0) child = NodeWithValue<T>.Read(stream);
            else if (typeId == 1) child = NodeWithChildren<T>.Read(stream, true);
            else child = NodeWithChildren<T>.Read(stream, false);
            node.Children[i] = child;
        }
        return node;
    }
}
