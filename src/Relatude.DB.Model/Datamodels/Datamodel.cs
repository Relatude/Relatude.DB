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

    /// <summary>
    /// What the source loader has to say about the sources it read: a source that turned out to hold
    /// no model types, a file or folder that is not there. None of it stops the model from loading - a
    /// source with nothing in it is a source that has nothing in it yet - but it is worth a line in the
    /// log and a note in the editor, since it is also what a typo looks like. Not serialized: it
    /// describes one load of the sources, not the model.
    /// </summary>
    [JsonIgnore]
    public readonly List<string> SourceNotices = new();

    [JsonIgnore] // not serialized
    public readonly HashSet<Assembly> Assemblies = new();

    // Emitted images of in-memory compiled model assemblies (simple name -> raw bytes).
    // Needed as metadata references when compiling mappers, since these assemblies have no Location.
    [JsonIgnore]
    public readonly Dictionary<string, byte[]> AssemblyImages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fills in the text search switches of every node type (TextIndex, SemanticIndex,
    /// InstantTextIndexing). A type that does not set one takes it from its base types - the most specific
    /// base that sets it, the way an overridden member is found in C# - and what is still unset after that
    /// is the database default given here. Changes the types in place, which is why only the store calls
    /// it: a model written back into its sources must keep a switch it inherits unset.
    /// </summary>
    public void SetIndexDefaults(bool enableTextIndexByDefault, bool enableSemanticIndexByDefault, bool enableInstantIndexing) {
        EnsureInitalization(); // the inheritance closures are needed
        var text = inheritedSwitches(t => t.TextIndex, nameof(NodeTypeModel.TextIndex));
        var semantic = inheritedSwitches(t => t.SemanticIndex, nameof(NodeTypeModel.SemanticIndex));
        var instant = inheritedSwitches(t => t.InstantTextIndexing, nameof(NodeTypeModel.InstantTextIndexing));
        foreach (var n in NodeTypes.Values) {
            n.TextIndex = text[n.Id];
            n.SemanticIndex = semantic[n.Id];
            n.InstantTextIndexing = instant[n.Id];
            if (n.SemanticIndex == true) n.TextIndex = true; // as for a type that sets it itself (see EnsureInitalization)
        }
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