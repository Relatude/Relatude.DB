using Relatude.DB.DataStores.Transactions;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.Transactions;
public class PrimitiveRelationAction : PrimitiveActionBase {
    public PrimitiveRelationAction(PrimitiveOperation operation, Guid relationId, int source, int target, DateTime dtUtc)
        : base(PrimitiveActionTarget.Relation) {
        Operation = operation;
        RelationId = relationId;
        Source = source;
        Target = target;
        ChangeUtc = dtUtc;
    }
    public Guid RelationId { get; }
    public int Source { get; }
    public int Target { get; }
    public DateTime ChangeUtc { get; }
    // runtime only, not serialized: set when a remove executes so the opposite add of a rollback can
    // restore the exact list positions instead of appending at the end (see RelationStore.RegisterAction)
    public int? RestoreSourceListIndex { get; set; }
    public int? RestoreTargetListIndex { get; set; }
    public override PrimitiveActionBase Opposite() {
        if (Operation == PrimitiveOperation.Add) {
            return new PrimitiveRelationAction(PrimitiveOperation.Remove, RelationId, Source, Target, ChangeUtc);
        } else {
            return new PrimitiveRelationAction(PrimitiveOperation.Add, RelationId, Source, Target, ChangeUtc) {
                RestoreSourceListIndex = RestoreSourceListIndex,
                RestoreTargetListIndex = RestoreTargetListIndex,
            };
        }
    }
    public override string ToString() => Operation + " Relation: " + Source + " -> " + Target;
}
