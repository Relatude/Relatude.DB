namespace Relatude.DB.Transactions;

//public enum ResultingNodeOperation : byte {
//    None = 0,
//    Created = 1,
//    Updated = 2,
//    Deleted = 3,
//}
//public enum ResultingNodePropertyOperation : byte {
//    None = 100,
//    Changed = 101,
//}
//public enum ResultingRelationOperation : byte {
//    None = 200,
//    Added = 201,
//    Removed = 202,
//}
//public enum AnyResultingOperation : byte { 
//    NodeNone = 0,
//    NodeCreated = 1,
//    NodeUpdated = 2,
//    NodeDeleted = 3,
//    NodePropertyNone = 100,
//    NodePropertyChanged = 101,
//    RelationNone = 200,
//    RelationAdded = 201,
//    RelationRemoved = 202,
//}
//public class ActionOperations {
//    public AnyResultingOperation[] Operations { get; set; } = [];
//}
//public class ResultingOperations {
//    public ActionOperations[] ActionOperations { get; set; } = [];
//}
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
