namespace Relatude.DB.DataStores;

/// <summary>
/// A query or read gave up waiting for the store lock after the time its context allowed
/// (<see cref="Datamodels.QueryContext.LockWait"/>). The lock was held by a transaction, which holds it
/// for as long as it runs, or by another writer. Nothing was read, and asking again later is safe.
/// </summary>
public class StoreBusyException(TimeSpan waited)
    : TimeoutException("The database is busy writing: a query waited " + (int)waited.TotalMilliseconds + "ms for a transaction to finish and gave up. ") {
    /// <summary>How long the query waited before it gave up.</summary>
    public TimeSpan Waited { get; } = waited;
}
