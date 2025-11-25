namespace Relatude.DB.Transactions;

public enum ResultingOperation {
    None, // no operation, e.g. InsertIfNotExists when node already exists
    CreateNode, // node was created, e.g. Insert or InsertIfNotExists when node did not exist
    UpdateNode, // node was updated, e.g. Update or Upsert when node existed and was different, change type, ( not reindex )
    DeleteNode, // node was deleted, e.g.

    AddedRelation, // a relation was added, e.g. RelationAction.Add
    RemovedRelation, // a relation was removed, e.g. RelationAction.Remove
    SetRelation, // a relation was set, e.g. RelationAction.Set
    RemovedAllRelations, // all relations were removed, e.g. RelationAction.RemoveAll
    RemovedAllRelationsFromSource, // all relations to source were removed, e.g. RelationAction.RemoveAllToSource
    RemovedAllRelationsToTarget, // all relations to target were removed, e.g. RelationAction.RemoveAllToTarget

    ChangedProperty, // a property was changed, e.g. PropertyUpdate if different, Add, Multiply
}
public class TransactionResult(long id, ResultingOperation[] resultingOperations) {
    public long TransactionId { get; } = id;
    public ResultingOperation[] ResultingOperations { get; } = resultingOperations;
    public static readonly TransactionResult Empty = new(0, []);
}
