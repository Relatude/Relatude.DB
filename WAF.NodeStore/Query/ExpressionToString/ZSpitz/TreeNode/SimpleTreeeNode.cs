using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WAF.Query.ExpressionToString.ZSpitz.TreeNode {
    public class SimpleTreeNode<TData> : TreeNodeBase<TData, SimpleTreeNode<TData>, ReadOnlyCollection<SimpleTreeNode<TData>>> {
        protected override (IList<SimpleTreeNode<TData>> innerCollection, ReadOnlyCollection<SimpleTreeNode<TData>> collectionWrapper) InitWith() {
            var innerCollection = new List<SimpleTreeNode<TData>>();
            var wrapper = new ReadOnlyCollection<SimpleTreeNode<TData>>(innerCollection);
            return (innerCollection, wrapper);
        }
    }
}
