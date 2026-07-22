using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.Transactions
{
    public class PrimitiveNodeAction : PrimitiveActionBase {
        public PrimitiveNodeAction(PrimitiveOperation operation, INodeDataInternal node)
            : base(PrimitiveActionTarget.Node) {
            Operation = operation; Node = node;
        }
        public INodeDataInternal Node { get; set; }
        public NodeSegment? Segment { get; set; }
        public override PrimitiveNodeAction Opposite() {
            return Operation switch {
                PrimitiveOperation.Add => new PrimitiveNodeAction(PrimitiveOperation.Remove, Node) { Segment = Segment },
                PrimitiveOperation.Remove => new PrimitiveNodeAction(PrimitiveOperation.Add, Node) { Segment = Segment }, // segment must be kept so the re-added node still points to its data in the log
                _ => throw new NotSupportedException(),
            };
        }
        public override string ToString() => Operation+ " Node: " + Node.__Id;
    }
}
