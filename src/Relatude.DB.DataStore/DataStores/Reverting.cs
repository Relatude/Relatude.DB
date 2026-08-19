namespace Relatude.DB.DataStores;

/// <summary>
/// The active revert window of a data store, see <see cref="IDataStore.BeginRevertWindow"/>. The
/// window marks a point in the transaction log that <see cref="IDataStore.RollbackRevertWindow"/>
/// can return to cheaply: while it is active the store suspends everything that would persist
/// state past the point (engine durability, state snapshots, log rewrites), so a rollback only has
/// to truncate the log and reload — no full index rebuild.
/// </summary>
public sealed class RevertWindowInfo {
    /// <summary>The rollback target: the log timestamp of the last transaction that survives a rollback.</summary>
    public required long Timestamp { get; init; }
    /// <summary>When the window was begun (wall clock, UTC).</summary>
    public required DateTime BegunUtc { get; init; }
    /// <summary>Byte position in the log file right after the last kept transaction — where a rollback truncates.</summary>
    public required long LogPosition { get; init; }
    /// <summary>The log file the window belongs to; a rollback refuses to run against a different file.</summary>
    public required Guid LogFileId { get; init; }
    /// <summary><see cref="Timestamp"/> as a UTC point in time (timestamps are UTC ticks).</summary>
    public DateTime TimestampUtc => new(Timestamp, DateTimeKind.Utc);
}

/// <summary>
/// What <see cref="IDataStore.DeleteTransactionsAfter"/> or
/// <see cref="IDataStore.RollbackRevertWindow"/> deleted (or, for a dry run, would delete).
/// </summary>
public sealed class DeleteTransactionsResult {
    /// <summary>True when nothing was changed: the numbers describe what a real run would do.</summary>
    public required bool DryRun { get; init; }
    /// <summary>The rollback target that was passed in: every transaction after it is deleted.</summary>
    public required long AfterTimestamp { get; init; }
    /// <summary>The head of the log after the operation (for a dry run: the head that would remain).</summary>
    public required long LastTimestamp { get; init; }
    public required int TransactionsDeleted { get; init; }
    public required int ActionsDeleted { get; init; }
    /// <summary>Bytes removed from the end of the log file.</summary>
    public required long BytesTruncated { get; init; }
    /// <summary>True when the state snapshot was newer than the rollback target, so the state and
    /// every index had to be rebuilt from the truncated log (the expensive path).</summary>
    public bool StateAndIndexesReset { get; init; }
    /// <summary>Names of the persisted index engines that held transactions newer than the target
    /// and were reset to be rebuilt from the truncated log.</summary>
    public string[] EnginesReset { get; init; } = [];
    public double DurationMs { get; init; }
    /// <summary><see cref="AfterTimestamp"/> as a UTC point in time.</summary>
    public DateTime AfterTimestampUtc => new(AfterTimestamp, DateTimeKind.Utc);
    /// <summary><see cref="LastTimestamp"/> as a UTC point in time.</summary>
    public DateTime LastTimestampUtc => new(LastTimestamp, DateTimeKind.Utc);
}
