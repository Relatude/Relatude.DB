using System.Reflection;
using System.Text.Json.Serialization;
namespace Relatude.DB.Datamodels;
public partial class Datamodel {
    public Datamodel() {
        var baseModel = new NodeTypeModel() {
            Id = NodeConstants.BaseNodeTypeId,
            CodeName = "INode",
            ModelType = ModelType.Interface,
            Namespace = "Relatude.Datamodels",
        };
        foreach (var p in getBaseProperties()) baseModel.Properties.Add(p.Id, p);
        NodeTypes.Add(baseModel.Id, baseModel);
    }
    public Dictionary<Guid, NodeTypeModel> NodeTypes { get; set; } = new();
    public Dictionary<Guid, RelationModel> Relations { get; set; } = new();

    [JsonIgnore] // not serialized
    public readonly HashSet<Assembly> Assemblies = new();

    public void SetIndexDefaults(bool enableTextIndexByDefault, bool enableSemanticIndexByDefault, bool enableInstantIndexing) {
        foreach (var n in NodeTypes.Values) {
            if (!n.TextIndex.HasValue)
                n.TextIndex = enableTextIndexByDefault;
            if (!n.SemanticIndex.HasValue) {
                n.SemanticIndex = enableSemanticIndexByDefault;
                if (enableSemanticIndexByDefault)
                    n.TextIndex = true;
            }
            if(!n.InstantTextIndexing.HasValue)
                n.InstantTextIndexing = enableInstantIndexing;
        }
    }
}