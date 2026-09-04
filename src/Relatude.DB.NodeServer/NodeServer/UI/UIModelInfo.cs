using Relatude.DB.Datamodels;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The bits of the data model that pages other than the model editor show: which sources there are,
/// and what each node type is. The admin UI marks a type with its kind (class, interface, record,
/// struct) and the colour of the source it came from wherever types are listed - the query page's
/// type picker, the dashboard's content panel - so the same type looks the same everywhere. The
/// colours themselves are a UI concern and are picked client side from the order of this list.
/// </summary>
static class UIModelInfo {
    public static object[] Sources(Datamodel datamodel) {
        return [.. datamodel.Sources.Select(s => (object)new {
            s.Id,
            Name = string.IsNullOrEmpty(s.Name) ? (s.Type == DatamodelSourceType.Code ? "Code" : s.Id.ToString()) : s.Name,
            Type = s.Type.ToString(),
        })];
    }
}
