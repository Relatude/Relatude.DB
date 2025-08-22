using static System.Linq.Enumerable;

namespace WAF.Query.ExpressionToString.ZSpitz.TreeNode {
    public abstract class TreeNodeBase<TData, TNode, TCollectionWrapper>
            where TNode : TreeNodeBase<TData, TNode, TCollectionWrapper>, new()
            where TCollectionWrapper : IReadOnlyCollection<TNode> {
        public TData? Data { get; set; }

        private readonly IList<TNode> _children;
        public TCollectionWrapper Children { get; }

        protected abstract (IList<TNode> innerCollection, TCollectionWrapper collectionWrapper) InitWith();

        public TreeNodeBase(TData? data = default, IEnumerable<TData>? children = default) {
            Data = data;
            _children = (children ?? Empty<TData>()).Select(x => new TNode() { Data = x }).ToList();
            (_children, Children) = InitWith();
        }

        private TNode? _parent;
        public TNode? Parent {
            get => _parent;
            set {
                if (_parent == value) { return; }
                var typedThis = (TNode)this;

                // I think it impossible for a given TreeNode to be a child twice of the parent node (assuming no thread-safety)
                // The inner List<TNode> is private, and is only modified by modifying the Parent of an existing node
                _parent?._children.Remove(typedThis);

                _parent = value;
                _parent?._children.Add(typedThis);
            }
        }

        public TNode AddChild(TData? data = default) => new() { Data = data, Parent = (TNode)this };
        public TNode? AddSibling(TData? data = default) => Parent?.AddChild(data);

        public IEnumerable<TNode> Parents() {
            var current = (TNode)this;
            while (current is { }) {
                yield return current;
                current = current.Parent;
            }
        }

        public IEnumerable<TNode> Descendants() {
            foreach (var child in Children) {
                yield return child;
                foreach (var descendant in child.Descendants()) {
                    yield return descendant;
                }
            }
        }
    }
}
