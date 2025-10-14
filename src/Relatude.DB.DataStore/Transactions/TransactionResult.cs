namespace Relatude.DB.Transactions;

public enum ResultingNodeOperation {
    None, 
    Created, 
    Changed,
    Deleted,
}
public enum ResultingNodePropertyOperation {
    None,
    Changed, 
}
public enum ResultingRelationOperation {
    None,
    Added, 
    Removed,
}
public class ActionOperations {
}
public class ResultingOperations {
    public ActionOperations[] ActionOperations { get; set; } = [];
}
public enum ResultingOperation {
    None, // no operation, e.g. InsertIfNotExists when node already exists
    CreateNode, // node was created, e.g. Insert or InsertIfNotExists when node did not exist
    UpdateNode, // node was updated, e.g. Update or Upsert when node existed and was different, change type, ( not reindex )
    DeleteNode, // node was deleted, e.g.
    AddedRelation, // a relation was added, e.g. RelationAction.Add
    RemovedRelation, // a relation was removed, e.g. RelationAction.Remove
    ChangedProperty, // a property was changed, e.g. PropertyUpdate if different, Add, Multiply
}
public class TransactionResult(long id, ResultingOperation[] resultingOperations) {
    public long TransactionId { get; } = id;
    public ResultingOperation[] ResultingOperations { get; } = resultingOperations;
    public static readonly TransactionResult Empty = new(0, []);
}
