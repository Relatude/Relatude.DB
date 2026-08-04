using Relatude.DB.Datamodels;

namespace Relatude.DB.Transactions;
public enum RelationOperation : byte {
    Add = 0, // throws exception if already exists
    Remove = 1, // throws exception if not exists
    Set = 2, // if already set to current value, does nothing. If set to different value, removes old value and adds new value
    Clear = 3, // removes relation if exists (source=0 => all targets, target=0 => all sources, target and source =0 => all!)
    // reordering of the owner's list of related nodes, all positions are clamped to the list bounds:
    MoveOffset = 4, // moves Items by Offset places within the owner's ordered list (negative = towards the top)
    MoveToTop = 5, // moves Items to the top of the owner's ordered list
    MoveToBottom = 6, // moves Items to the bottom of the owner's ordered list
    MoveBefore = 7, // moves Items to just before Anchor in the owner's ordered list
    MoveAfter = 8, // moves Items to just after Anchor in the owner's ordered list
    SetOrder = 9, // reorders the owner's list to match Items exactly (Items must contain exactly the currently related ids)
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
    // fields for the move operations only:
    public int Owner; // the node whose ordered list of related nodes is changed
    public Guid OwnerGuid;
    public int[]? Items; // the related nodes to move, order is irrelevant except for SetOrder
    public Guid[]? ItemGuids;
    public int Anchor; // for MoveBefore and MoveAfter
    public Guid AnchorGuid;
    public int Offset; // for MoveOffset
    public bool ReorderSourcesOfTarget; // false: owner is a source and its target list is reordered, true: owner is a target and its source list is reordered
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
            case RelationOperation.MoveOffset:
                return $"Move {itemCount()} item(s) of relation {relationName} of {ownerDescription()} by {Offset} place(s). ";
            case RelationOperation.MoveToTop:
                return $"Move {itemCount()} item(s) of relation {relationName} of {ownerDescription()} to the top. ";
            case RelationOperation.MoveToBottom:
                return $"Move {itemCount()} item(s) of relation {relationName} of {ownerDescription()} to the bottom. ";
            case RelationOperation.MoveBefore:
                return $"Move {itemCount()} item(s) of relation {relationName} of {ownerDescription()} before anchor. ";
            case RelationOperation.MoveAfter:
                return $"Move {itemCount()} item(s) of relation {relationName} of {ownerDescription()} after anchor. ";
            case RelationOperation.SetOrder:
                return $"Set order of {itemCount()} item(s) of relation {relationName} of {ownerDescription()}. ";
            default:
                throw new NotImplementedException();
        }
    }
    int itemCount() => Items?.Length ?? ItemGuids?.Length ?? 0;
    string ownerDescription() => OwnerGuid != Guid.Empty ? OwnerGuid.ToString() : Owner.ToString();
    public override string OperationName() => "RelationAction." + Operation.ToString();
}
