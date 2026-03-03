using Relatude.DB.Common;
using Relatude.DB.Datamodels;
namespace Relatude.DB.Transactions;

public enum NodeRevisionOperation : byte {
    UpdateMeta, // updates the meta data
    EnableRevisions, // enables a revision for the node
    DisableRevisions, // disables a revision for the node
    CreateRevision, // inserts or updates a revision for the node based on the node provided
    DeleteRevision, // deletes a revision for the node
}
public class NodeRevisionAndMetaAction : ActionBase {

    public static NodeRevisionAndMetaAction UpdateMeta(IdKey key, int revisionId, INodeMeta? meta)
        => new(NodeRevisionOperation.UpdateMeta, key, revisionId, null, meta, null, null);
    public static NodeRevisionAndMetaAction DeleteRevision(IdKey key, int revisionId, Guid? cultureId)
        => new(NodeRevisionOperation.DeleteRevision, key, revisionId, null, null, cultureId, null);
    public static NodeRevisionAndMetaAction EnableRevisions(IdKey key, int revisionId, Guid? cultureId)
        => new(NodeRevisionOperation.EnableRevisions, key, revisionId, null, null, cultureId, null);
    public static NodeRevisionAndMetaAction DisableRevisions(IdKey key, int revisionIdToKeep, Guid? cultureIdToKeep)
        => new(NodeRevisionOperation.DisableRevisions, key, revisionIdToKeep, null, null,cultureIdToKeep, null);
    public static NodeRevisionAndMetaAction CreateRevision(IdKey key, int newRevisionId, int sourceRevisionId, Guid? cultureId, Guid? sourceCultureId)
        => new(NodeRevisionOperation.CreateRevision, key, newRevisionId, sourceRevisionId, null, cultureId, sourceCultureId);

    private NodeRevisionAndMetaAction(NodeRevisionOperation operation, IdKey idKey, int revisionId, int? sourceRevisionId, INodeMeta? meta, Guid? cultureId, Guid? sourceCultureId)
        : base(ActionTarget.NodeRevision) {
        Operation = operation;
        NodeIdKey = idKey;
        RevisionId = revisionId;
        SourceRevisionId = sourceRevisionId;
        CultureId = cultureId;
        Meta = meta;
        SourceCultureId = sourceCultureId;
    }
    public NodeRevisionOperation Operation { get; }
    public IdKey NodeIdKey { get; }
    public INodeMeta? Meta { get; }
    
    public int RevisionId { get; }
    public Guid? CultureId { get; }
    
    public int? SourceRevisionId { get; }
    public Guid? SourceCultureId { get; }
    //public RevisionType? RevisionType { get; }
    public override string ToString() => Operation.ToString().Decamelize();
    public override string OperationName() => "NodeRevisionAction." + Operation.ToString();
    public override string ToString(Datamodel dm) => ToString();
}
