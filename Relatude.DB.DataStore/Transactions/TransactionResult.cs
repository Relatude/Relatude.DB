namespace Relatude.DB.Transactions; 
public enum ResultingOperation {
    None, // no operation, e.g. InsertIfNotExists when node already exists
    CreateNode, // node was created, e.g. Insert or InsertIfNotExists when node did not exist
    UpdateNode, // node was updated, e.g. Update or Upsert when node existed and was different, change type, ( not reindex )
    DeleteNode, // node was deleted, e.g.
    ChangedProperty, // a property was changed, e.g. PropertyUpdate if different, Add, Multiply
}
public class TransactionResult(long id, ResultingOperation[] resultingOperations) {
    public long TransactionId { get; } = id;
    public ResultingOperation[] ResultingOperations { get; } = resultingOperations;
    public static readonly TransactionResult Empty = new(0, []);
}
