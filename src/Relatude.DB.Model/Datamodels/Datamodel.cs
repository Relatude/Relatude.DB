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

    /// <summary>
    /// Metadata about the datamodel sources this model was combined from. Types and relations
    /// refer back to these through their DatamodelSourceId.
    /// </summary>
    public List<DatamodelSource> Sources { get; set; } = new();

    /// <summary>
    /// The source id assigned to types and relations as they are added. Set by the source loader
    /// while a configured source is loading; outside of that it is DatamodelSource.CodeSourceId,
    /// so types added directly from code (e.g. in the OnDatamodelInit event) are tagged as code.
    /// </summary>
    [JsonIgnore]
    public Guid CurrentSourceId { get; set; } = DatamodelSource.CodeSourceId;

    [JsonIgnore] // not serialized
    public readonly HashSet<Assembly> Assemblies = new();

    // Emitted images of in-memory compiled model assemblies (simple name -> raw bytes).
    // Needed as metadata references when compiling mappers, since these assemblies have no Location.
    [JsonIgnore]
    public readonly Dictionary<string, byte[]> AssemblyImages = new(StringComparer.OrdinalIgnoreCase);

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