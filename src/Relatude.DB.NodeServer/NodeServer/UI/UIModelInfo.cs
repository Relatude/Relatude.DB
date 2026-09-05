using Relatude.DB.Datamodels;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The bits of the data model that pages other than the model editor show: which sources there are,
/// and what each node type is. The admin UI marks a type with its kind (class, interface, record,
/// struct) and the colour of the source it came from wherever types are listed - the query page's
/// type picker, the dashboard's content panel - so the same type looks the same everywhere. A source
/// may name its own colour; the ones that do not are coloured client side from the order of this list.
/// </summary>
static class UIModelInfo {
    /// <param name="settings">The database's settings, when at hand: a colour is UI only, so it is
    /// read from the settings as they are now rather than from the copy of the source the model was
    /// opened with - recolouring a source is not worth reopening a database for.</param>
    public static object[] Sources(Datamodel datamodel, NodeStoreContainerSettings? settings = null) {
        var configured = (settings?.DatamodelSources ?? []).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Color);
        return [.. datamodel.Sources.Select(s => (object)new {
            s.Id,
            Name = string.IsNullOrEmpty(s.Name) ? (s.Type == DatamodelSourceType.Code ? "Code" : s.Id.ToString()) : s.Name,
            Type = s.Type.ToString(),
            Color = configured.TryGetValue(s.Id, out var color) ? color : s.Color,
        })];
    }
}
