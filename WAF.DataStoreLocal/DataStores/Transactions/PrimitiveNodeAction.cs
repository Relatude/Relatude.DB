using WAF.Datamodels;
using WAF.DataStores.Transactions;
using WAF.Transactions;

namespace WAF.DataStores.Transactions
{
    public class PrimitiveNodeAction : PrimitiveActionBase {
        public PrimitiveNodeAction(PrimitiveOperation operation, INodeData node)
            : base(PrimitiveActionTarget.Node) {
            Operation = operation; Node = node;
        }
        public INodeData Node { get; set; }
        public NodeSegment? Segment { get; set; }
        public override PrimitiveNodeAction Opposite() {
            return Operation switch {
                PrimitiveOperation.Add => new PrimitiveNodeAction(PrimitiveOperation.Remove, Node),
                PrimitiveOperation.Remove => new PrimitiveNodeAction(PrimitiveOperation.Add, Node),
                _ => throw new NotSupportedException(),
            };
        }
        public override string ToString() => Operation+ " Node: " + Node.__Id;
    }
}
