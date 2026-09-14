using Relatude.DB.Common;
using Relatude.DB.DataStores;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// What one database is allowed to keep in memory and what it holds right now, budget by budget.
///
/// The budgets are spread over settings that are otherwise far apart - two cache sizes, one per
/// index engine, one for the state store - and each is a bound on a different structure, so the
/// only way to size any of them is to see what it is actually holding. That is what this page is:
/// every budget with its usage beside it, live.
///
/// A budget moved here takes effect at once where the component can be re-sized while it runs
/// (<see cref="IDataStore.TrySetMemoryBudget"/>) and is otherwise the settings value it will open
/// with next time. Saving is a settings edit like any other and goes through the settings command,
/// so the file, the overlay rules and the change log stay in one place.
/// </summary>
sealed class UIMemory {
    readonly RelatudeDBServer _server;
    internal UIMemory(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("memory", ctx => report(ctx.Payload<MemoryPayload>().StoreId));
        commands.Register("memory-apply", ctx => apply(ctx.Payload<ApplyMemoryPayload>()));
    }

    NodeStoreContainer container(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        return c;
    }

    object report(Guid storeId) {
        var c = container(storeId);
        var open = c.Store != null && c.Store.State == DataStoreState.Open;
        var local = c.Settings.LocalSettings;
        var budgets = open ? c.Store!.Datastore.GetMemoryBudgets() : [];
        return new {
            Open = open,
            State = c.HasFailed ? "Error" : c.Store?.State.ToString() ?? "Closed",
            ManagedBytes = GC.GetTotalMemory(false),
            ProcessBytes = workingSet(),
            Budgets = budgets.Select(b => view(b, local)).ToArray(),
        };
    }

    object apply(ApplyMemoryPayload payload) {
        var c = container(payload.StoreId);
        var store = c.Store ?? throw new Exception("The database must be open. ");
        if (store.State != DataStoreState.Open) throw new Exception("The database must be open. ");
        if (payload.Bytes < 0) throw new Exception("A budget cannot be negative. ");
        if (!Enum.TryParse<MemoryBudgetKind>(payload.Kind, ignoreCase: true, out var kind)) throw new Exception("Unknown budget: " + payload.Kind);
        var applied = store.Datastore.TrySetMemoryBudget(kind, payload.EngineId, payload.Bytes);
        return new { Applied = applied, Report = report(payload.StoreId) };
    }

    object view(MemoryBudget budget, SettingsLocal? local) {
        var (path, unit) = setting(budget);
        var settingBytes = configured(budget, local);
        return new {
            // one budget of one engine: the browser keys its slider on this
            Key = budget.Kind + (budget.EngineId == Guid.Empty ? "" : ":" + budget.EngineId.ToString("N")),
            Kind = budget.Kind.ToString(),
            budget.EngineId,
            budget.Label,
            budget.Engine,
            budget.LimitBytes,
            budget.UsedBytes,
            budget.FloorBytes,
            budget.Adjustable,
            SettingPath = path,
            SettingUnit = unit,
            SettingBytes = settingBytes,
            // where the slider ends: four times what the database is set up with, so the current
            // value sits a quarter along and there is room to raise it
            SuggestedMaxBytes = suggestedMax(Math.Max(settingBytes, budget.LimitBytes)),
            Help = help(budget.Kind),
        };
    }

    /// <summary>The setting behind a budget, and the unit it is written in. Null for a component with no budget to set.</summary>
    static (string? Path, string? Unit) setting(MemoryBudget budget) {
        if (budget.Kind is MemoryBudgetKind.NodeCache) return ("LocalSettings.NodeCacheSizeGb", "GB");
        if (budget.Kind is MemoryBudgetKind.SetCache) return ("LocalSettings.SetCacheSizeGb", "GB");
        if (budget.Kind is MemoryBudgetKind.StateStore) {
            return budget.Engine == "Memory" ? (null, null) : ("LocalSettings.StateStoreMaxMemoryUsageInMb", "MB");
        }
        if (budget.EngineId == Guid.Empty) return (null, null); // the memory index: nothing to bound
        var list = budget.Kind switch {
            MemoryBudgetKind.ValueIndex => "ValueIndexes",
            MemoryBudgetKind.TextIndex => "TextIndexes",
            MemoryBudgetKind.VectorIndex => "VectorIndexes",
            _ => null,
        };
        return list == null ? (null, null) : ("LocalSettings." + list + "[" + budget.EngineId + "].MaxMemoryUsageInMb", "MB");
    }

    /// <summary>What the settings file says this budget is, which is what the database opens with.</summary>
    static long configured(MemoryBudget budget, SettingsLocal? local) {
        if (local == null) return 0;
        const long gb = 1024L * 1024 * 1024;
        const long mb = 1024L * 1024;
        switch (budget.Kind) {
            case MemoryBudgetKind.NodeCache: return (long)(local.NodeCacheSizeGb * gb);
            case MemoryBudgetKind.SetCache: return (long)(local.SetCacheSizeGb * gb);
            case MemoryBudgetKind.StateStore: return local.StateStoreMaxMemoryUsageInMb * mb;
            default:
                var engines = budget.Kind switch {
                    MemoryBudgetKind.ValueIndex => local.ValueIndexes,
                    MemoryBudgetKind.TextIndex => local.TextIndexes,
                    MemoryBudgetKind.VectorIndex => local.VectorIndexes,
                    _ => null,
                };
                return SettingsLocal.FindIndexEngine(engines, budget.EngineId)?.MaxMemoryUsageInBytes ?? 0;
        }
    }

    // a round number above four times the current one, so the budget sits a quarter along the track
    // and the steps either side of it are worth taking
    static long suggestedMax(long bytes) {
        const long mb = 1024L * 1024;
        var wanted = Math.Max(128 * mb, bytes * 4);
        foreach (var steps in new[] { 128L, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 }) {
            if (wanted <= steps * mb) return steps * mb;
        }
        return 65536 * mb;
    }

    static string help(MemoryBudgetKind kind) => kind switch {
        MemoryBudgetKind.NodeCache =>
            "Decoded nodes, so a node read twice is only read from the log once. The first thing to raise on a database that reads the same content over and over, and the first to lower when the process is too big.",
        MemoryBudgetKind.SetCache =>
            "The id sets queries build: filters, facet counts, sorted results. This is what makes a repeated or drilled-into query answer without touching the indexes again.",
        MemoryBudgetKind.ValueIndex =>
            "The value index engine's page cache and write buffers. Raising it keeps more of the index pages in memory; lowering it means more reads from disk on filters and sorts.",
        MemoryBudgetKind.TextIndex =>
            "Decoded dictionary blocks and postings of the full-text index. Free-text search over a large corpus is what fills it.",
        MemoryBudgetKind.VectorIndex =>
            "What the semantic index keeps resident. Part of it is a floor the index never gives back (a graph index keeps its graph in memory), so a budget below that floor is exceeded by design.",
        MemoryBudgetKind.StateStore =>
            "The state store's page cache: the guid map, each node's position in the log, addresses and relations. Only used when the state store is on disk; in memory mode the maps are all resident and this does nothing.",
        _ => "",
    };

    static long workingSet() {
        try {
            return Environment.WorkingSet;
        } catch {
            return 0;
        }
    }

    sealed record MemoryPayload(Guid StoreId);
    sealed record ApplyMemoryPayload(Guid StoreId, string Kind, Guid EngineId, long Bytes);
}
