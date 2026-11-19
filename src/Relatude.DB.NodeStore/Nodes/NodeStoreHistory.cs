using Relatude.DB.DataStores;

namespace Relatude.DB.Nodes;

/// <summary>
/// The change history of a <see cref="NodeStore"/> (<see cref="NodeStore.History"/>): what <see cref="IDataStore"/>'s
/// history methods answer, asked in the store's own context.
/// </summary>
public sealed class NodeStoreHistory {
    readonly NodeStore _store;
    internal NodeStoreHistory(NodeStore store) => _store = store;
    /// <summary>Whether the history is on, how far back it reaches and how far it has got.</summary>
    public LogHistoryStatus Status => _store.Datastore.GetLogHistoryStatus();
    /// <summary>A page of changes, newest first: give the page's <see cref="LogChangePage.Next"/> as the next query's cursor.</summary>
    public LogChangePage Read(LogChangeQuery query) => _store.Datastore.ReadLogChanges(query);
    /// <summary>How much changed when, in bars.</summary>
    public LogActivity Analyse(LogActivityQuery query) => _store.Datastore.AnalyseLogChanges(query);
    /// <summary>A node as one save left it, mapped to <typeparamref name="T"/>; null when the history does not have it.</summary>
    public T? VersionAt<T>(Guid nodeId, long timestamp) where T : class {
        var version = _store.Datastore.FindVersionAt(nodeId, timestamp);
        return version == null ? null : _store.Mapper.CreateObjectFromNodeData<T>(version.Node, null, _store);
    }
    /// <summary>The node's data as one save left it; null when the history does not have it.</summary>
    public NodeVersionData? VersionDataAt(Guid nodeId, long timestamp) => _store.Datastore.FindVersionAt(nodeId, timestamp);
    /// <summary>Builds the history index again from the log, in the background.</summary>
    public void Rebuild() => _store.Datastore.RebuildLogHistory();
}
