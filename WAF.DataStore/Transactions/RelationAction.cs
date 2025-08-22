using System.ComponentModel;
using WAF.Datamodels;

namespace WAF.Transactions;
public enum RelationOperation : byte {
    Add = 0, // throws exception if already exists
    Remove = 1, // throws exception if not exists
    Set = 2, // if already set to current value, does nothing. If set to different value, removes old value and adds new value
    Clear = 3, // removes relation if exists
}
public class RelationAction : ActionBase {
    public RelationAction(RelationOperation operation, Guid relationId)
        : base(ActionTarget.Relation) {
        Operation = operation; RelationId = relationId;
    }
    public RelationOperation Operation;
    public Guid RelationId;
    public int Source;
    public int Target;
    public Guid SourceGuid;
    public Guid TargetGuid;
    public DateTime ChangeUtc;
    public override string ToString() => toString(RelationId.ToString());
    public override string ToString(Datamodel dm) {
        if (dm.Relations.TryGetValue(RelationId, out var relation)) {
            return toString((string.IsNullOrEmpty(relation.Namespace) ? "" : relation.Namespace + ".") + relation.CodeName);
        } else {
            return toString(RelationId.ToString());
        }
    }
    string toString(string relationName) {
        switch (Operation) {
            case RelationOperation.Add:
                return $"Add relation {relationName} from {SourceGuid} to {TargetGuid}. ";
            case RelationOperation.Remove:
                return $"Remove relation {relationName} from {SourceGuid} to {TargetGuid}. ";
            case RelationOperation.Set:
                return $"Set relation {relationName} from {SourceGuid} to {TargetGuid}. ";
            case RelationOperation.Clear:
                return $"Clear relation {relationName} from {SourceGuid} to {TargetGuid}. ";
            default:
                throw new NotImplementedException();
        }
    }
    public override string OperationName() => "RelationAction." + Operation.ToString();
}
