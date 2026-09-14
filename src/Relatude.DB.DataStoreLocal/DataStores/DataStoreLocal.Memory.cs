using Relatude.DB.DataStores.Indexes;

namespace Relatude.DB.DataStores;

// What this database is allowed to keep in memory, and what it keeps right now. Every budget is a
// bound the component works within, never an allocation: a database that has not needed the memory
// has not taken it, which is why a budget and its usage are always reported together.
//
// A budget changed here holds for as long as the database stays open. Persisting it is a settings
// edit and stays with the settings, so the two can differ on purpose: raising a budget to see what
// it does costs a slider, not a restart.
public sealed partial class DataStoreLocal : IDataStore {
    public MemoryBudget[] GetMemoryBudgets() {
        validateDatabaseState();
        List<MemoryBudget> budgets = [
            new() {
                Kind = MemoryBudgetKind.NodeCache, Label = "Node cache", Engine = "Memory",
                LimitBytes = _nodes.CacheMaxSize, UsedBytes = _nodes.CacheSize, Adjustable = true,
            },
            new() {
                Kind = MemoryBudgetKind.SetCache, Label = "Result set cache", Engine = "Memory",
                LimitBytes = _sets.SetCacheMaxBytes, UsedBytes = _sets.SetCacheBytes, Adjustable = true,
            },
            indexBudget(MemoryBudgetKind.ValueIndex, "Value indexes", _settings.DefaultValueIndex, _settings.DefaultValueEngine, Engines.ValueEngine(_settings.DefaultValueIndex)),
            indexBudget(MemoryBudgetKind.TextIndex, "Text indexes", _settings.DefaultTextIndex, _settings.DefaultTextEngine, Engines.TextEngine(_settings.DefaultTextIndex)),
        ];
        if (_ai != null) budgets.Add(vectorBudget());
        budgets.Add(stateStoreBudget());
        return [.. budgets];
    }

    static MemoryBudget indexBudget(MemoryBudgetKind kind, string label, Guid engineId, IndexEngineSettings? settings, IIndexEngine? engine) {
        if (engine == null || settings == null) return memoryIndexBudget(kind, label);
        var budget = engine.GetMemoryBudget();
        return new() {
            Kind = kind, EngineId = engineId, Label = label, Engine = settings.TypeName ?? engine.Name,
            LimitBytes = budget >= 0 ? budget : settings.MaxMemoryUsageInBytes,
            UsedBytes = engine.GetMemoryUsage(),
            Adjustable = budget >= 0, // an engine that keeps its own budget is one that can be given another
        };
    }

    MemoryBudget vectorBudget() {
        var engine = Engines.VectorEngine(_settings.DefaultVectorIndex);
        var settings = _settings.DefaultVectorEngine;
        if (engine == null || settings == null) return memoryIndexBudget(MemoryBudgetKind.VectorIndex, "Semantic indexes");
        var budget = engine.GetMemoryBudget();
        return new() {
            Kind = MemoryBudgetKind.VectorIndex, EngineId = _settings.DefaultVectorIndex, Label = "Semantic indexes",
            Engine = settings.TypeName ?? engine.Name,
            LimitBytes = budget >= 0 ? budget : settings.MaxMemoryUsageInBytes,
            UsedBytes = engine.GetMemoryUsage(),
            FloorBytes = engine.GetMemoryFloor(),
            Adjustable = budget >= 0,
        };
    }

    MemoryBudget stateStoreBudget() {
        var engine = _stateStore.Engine;
        if (engine == null) {
            return new() {
                Kind = MemoryBudgetKind.StateStore, Label = "State store", Engine = "Memory",
                LimitBytes = 0, UsedBytes = null, Adjustable = false,
            };
        }
        var budget = engine.GetMemoryBudget();
        return new() {
            Kind = MemoryBudgetKind.StateStore, Label = "State store", Engine = _settings.StateStore.ToString(),
            LimitBytes = budget >= 0 ? budget : _settings.StateStoreMaxMemoryUsageInMb * 1024L * 1024L,
            UsedBytes = engine.GetMemoryUsage(),
            Adjustable = budget >= 0,
        };
    }

    // a kind left in memory has no budget to spend and nothing to measure: it is resident, and what
    // it costs is the heap
    static MemoryBudget memoryIndexBudget(MemoryBudgetKind kind, string label) => new() {
        Kind = kind, Label = label, Engine = "Memory", LimitBytes = 0, UsedBytes = null, Adjustable = false,
    };

    public bool TrySetMemoryBudget(MemoryBudgetKind kind, Guid engineId, long bytes) {
        validateDatabaseState();
        if (bytes < 0) return false;
        switch (kind) {
            case MemoryBudgetKind.NodeCache:
                _nodes.SetCacheMaxSize(bytes);
                return true;
            case MemoryBudgetKind.SetCache:
                _sets.SetSetCacheMaxBytes(bytes);
                return true;
            case MemoryBudgetKind.ValueIndex:
                return Engines.ValueEngine(engineId)?.TrySetMemoryBudget(bytes) ?? false;
            case MemoryBudgetKind.TextIndex:
                return Engines.TextEngine(engineId)?.TrySetMemoryBudget(bytes) ?? false;
            case MemoryBudgetKind.VectorIndex:
                return Engines.VectorEngine(engineId)?.TrySetMemoryBudget(bytes) ?? false;
            case MemoryBudgetKind.StateStore:
                return _stateStore.Engine?.TrySetMemoryBudget(bytes) ?? false;
            default:
                return false;
        }
    }
}
