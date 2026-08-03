namespace Relatude.DB.DataStores.Transactions;
public class PrimitiveRelationReorderAction : PrimitiveActionBase {
    public PrimitiveRelationReorderAction(Guid relationId, int owner, int moved, bool fromTargetToSource, int fromIndex, int toIndex)
        : base(PrimitiveActionTarget.RelationOrder) {
        Operation = PrimitiveOperation.Move;
        RelationId = relationId;
        Owner = owner;
        Moved = moved;
        FromTargetToSource = fromTargetToSource;
        FromIndex = fromIndex;
        ToIndex = toIndex;
    }
    public Guid RelationId { get; }
    /// <summary>The node whose ordered list of related nodes is changed. </summary>
    public int Owner { get; }
    /// <summary>The related node that is moved within the owner's list. </summary>
    public int Moved { get; }
    /// <summary>Direction of the reordered list, same convention as IRelationIndex.Get(owner, fromTargetToSource). </summary>
    public bool FromTargetToSource { get; }
    public int FromIndex { get; }
    public int ToIndex { get; }
    public override PrimitiveActionBase Opposite() {
        return new PrimitiveRelationReorderAction(RelationId, Owner, Moved, FromTargetToSource, ToIndex, FromIndex);
    }
    public override string ToString() => "Move relation item " + Moved + " of " + Owner + " from position " + FromIndex + " to " + ToIndex;
}
