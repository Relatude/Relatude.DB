using WAF.DataStores.Transactions;
using WAF.Transactions;

namespace WAF.DataStores.Transactions;
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
    public override PrimitiveActionBase Opposite() {
        if (Operation == PrimitiveOperation.Add) {
            return new PrimitiveRelationAction(PrimitiveOperation.Remove, RelationId, Source, Target, ChangeUtc);
        } else {
            return new PrimitiveRelationAction(PrimitiveOperation.Add, RelationId, Source, Target, ChangeUtc);
        }
    }
    public override string ToString() => Operation + " Relation: " + Source + " -> " + Target;
}
