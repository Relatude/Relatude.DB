using Relatude.DB.Transactions;
using Relatude.DB.DataStores.Transactions;
namespace Relatude.DB.DataStores.Transactions;
/// <summary>
/// What kind of write a transaction in the log is, kept in its header from log format 1002 on.
/// </summary>
[Flags]
internal enum TransactionLogFlags : byte {
    None = 0,
    /// <summary>A copy of nodes and relations as they were, written by a log rewrite (a truncation) to start
    /// the new file: no change was made by it.</summary>
    Copy = 1,
    /// <summary>A write the database makes for itself (text indexing, file analysis, measuring): no person made it.</summary>
    System = 2,
}
internal class ExecutedPrimitiveTransaction(List<PrimitiveActionBase> executedActions, long timestamp) {
    public ExecutedPrimitiveTransaction(List<PrimitiveActionBase> executedActions, long timestamp, Guid writer, TransactionLogFlags flags)
        : this(executedActions, timestamp) {
        Writer = writer;
        Flags = flags;
    }
    public List<PrimitiveActionBase> ExecutedActions { get; } = executedActions;
    public long Timestamp { get; } = timestamp;
    /// <summary>Who made the transaction: the user of the context it ran in, Guid.Empty for code with no user.
    /// Written to the log from format 1002 on; read back as Guid.Empty from older files.</summary>
    public Guid Writer { get; init; }
    public TransactionLogFlags Flags { get; init; }
}
