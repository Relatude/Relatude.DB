namespace Relatude.DB.DataStores.Indexes.Trie.TrieNet._Ukkonen {
    internal class Edge<T> {
        public Edge(string label, Node<T> target) {
            Label = label;
            Target = target;
        }

        public string Label { get; set; }

        public Node<T> Target { get; private set; }
    }
}