namespace Relatude.DB.DataStores;

/// <summary>Which part of a database a <see cref="MemoryBudget"/> belongs to.</summary>
public enum MemoryBudgetKind {
    NodeCache,
    SetCache,
    ValueIndex,
    TextIndex,
    VectorIndex,
    StateStore,
}

/// <summary>
/// One memory budget of a database: what it is for, what it may spend, and what it holds right now.
/// A budget is a bound the component works within, never an allocation, so a database that has not
/// needed the memory has not taken it.
/// </summary>
public sealed class MemoryBudget {
    public required MemoryBudgetKind Kind { get; init; }
    /// <summary>The index engine this budget belongs to, or <see cref="Guid.Empty"/> for the caches and the state store.</summary>
    public Guid EngineId { get; init; }
    public required string Label { get; init; }
    /// <summary>What is behind it: the engine's type name, or "Memory" for something that only ever lives in RAM.</summary>
    public required string Engine { get; init; }
    /// <summary>The bound in bytes, or 0 for a component that has none (the memory indexes).</summary>
    public long LimitBytes { get; init; }
    /// <summary>What it holds right now, or null when the component cannot measure it.</summary>
    public long? UsedBytes { get; init; }
    /// <summary>What it keeps whatever the budget says (the resident part of a vector index); 0 when it can give everything back.</summary>
    public long FloorBytes { get; init; }
    /// <summary>True when a new budget takes effect without reopening the database.</summary>
    public bool Adjustable { get; init; }
}
