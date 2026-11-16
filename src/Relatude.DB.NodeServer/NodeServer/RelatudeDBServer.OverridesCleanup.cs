using System.Text.Json.Nodes;
using Relatude.DB.GraphQL.Endpoints;
using Relatude.DB.IO;
using Relatude.DB.Logging;

namespace Relatude.DB.NodeServer;

/// <summary>
/// What a database's DATA holds that changes nothing goes when the database opens - typically after its
/// entries were put into the SETTINGS files by hand (the overrides download) and deployed: datamodel
/// overrides that say what datamodel.json says, and log and endpoint definitions that say what the
/// settings folder's say, or take away one it no longer has. Each removal is logged. Runtime types are
/// not touched. relatude.db.overrides.json and the custom settings are cleaned the same way where they
/// are read.
/// </summary>
public partial class RelatudeDBServer {
    internal void RemoveRedundantOverrides(NodeStoreContainer container) {
        var settings = container.Settings;
        var name = nameOf(settings);
        try {
            lock (container.OverridesWriteLock) removeRedundantDatamodelOverrides(container.OverridesFile, name);
        } catch (Exception err) {
            startupWarning("Could not take what datamodel.json now says out of the datamodel overrides of \"" + name + "\": " + err.Message);
        }
        removeRedundantDefinitions(LogDefinitionsFor(settings), CustomLogs.IdOf, "log", name);
        if (removeRedundantDefinitions(GraphQLDefinitionsFor(settings), GraphQLEndpointStore.IdOf, "GraphQL endpoint", name)) _graphQL?.Invalidate();
    }

    void removeRedundantDatamodelOverrides(DatamodelOverridesFile file, string name) {
        if (file.IoId == null || !TryGetIO(file.IoId.Value, out var io)) return;
        var settingsLayer = file.ReadSettingsLayer();
        var data = file.ReadDataLayer(this);
        var redundant = DatamodelOverridesLayers.Entries(data).Where(e => {
            var shared = DatamodelOverridesLayers.Get(settingsLayer, new(e.TypeId, e.PropertyId, e.Attribute), out var inSettings);
            return inSettings ? JsonNode.DeepEquals(shared, e.Value) : e.Value == null;
        }).ToList();
        if (redundant.Count == 0) return;
        var (_, rest, _) = DatamodelOverridesLayers.Move(settingsLayer, data, redundant.Select(e => new DatamodelOverridesLayers.AttributePath(e.TypeId, e.PropertyId, e.Attribute)));
        if (DatamodelOverridesLayers.IsEmpty(rest)) io.DeleteFileIfItExists(file.IoKey);
        else io.WriteAllTextUTF8(file.IoKey, file.DataText(rest));
        Log("Taken out of the datamodel overrides of \"" + name + "\" in " + file.DataLocation + ", as " + file.SettingsLocation + " now says the same: "
            + string.Join(", ", redundant.Select(e => (e.PropertyName ?? e.TypeName ?? e.TypeId.ToString()) + "." + e.Attribute)) + ".");
    }

    bool removeRedundantDefinitions(LayeredDefinitionFolders? folders, Func<string[], JsonObject, string> idOf, string kind, string name) {
        if (folders == null) return false;
        try {
            var (definitions, _) = LayeredDefinitions.Read(folders, idOf, StringComparer.OrdinalIgnoreCase);
            var removed = new List<string>();
            foreach (var definition in LayeredDefinitions.Redundant(definitions)) {
                if (LayeredDefinitions.DiscardData(folders, definition)) removed.Add(folders.Data.DisplayOf(definition.DataKey!));
            }
            if (removed.Count == 0) return false;
            Log("Taken out of the " + kind + " definitions of \"" + name + "\", as " + folders.Settings?.Display + " now says the same: " + string.Join(", ", removed) + ".");
            return true;
        } catch (Exception err) {
            startupWarning("Could not take what " + folders.Settings?.Display + " now says out of the " + kind + " definitions of \"" + name + "\": " + err.Message);
            return false;
        }
    }
}
