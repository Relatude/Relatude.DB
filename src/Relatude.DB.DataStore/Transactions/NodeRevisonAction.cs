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

    public static NodeRevisionAndMetaAction UpdateMeta(IdKey key, Guid revisionId, INodeMeta meta)
        => new(NodeRevisionOperation.UpdateMeta, key, revisionId, null, meta, null, null);
    public static NodeRevisionAndMetaAction DeleteRevision(IdKey key, Guid revisionId)
        => new(NodeRevisionOperation.DeleteRevision, key, revisionId, null, null, null, null);
    public static NodeRevisionAndMetaAction EnableRevisions(IdKey key, Guid revisionId, RevisionType revisionType)
        => new(NodeRevisionOperation.EnableRevisions, key, revisionId, null, null, null, null);
    public static NodeRevisionAndMetaAction DisableRevisions(IdKey key, Guid revisionIdToKeep)
        => new(NodeRevisionOperation.DisableRevisions, key, revisionIdToKeep, null, null, null, null);
    public static NodeRevisionAndMetaAction CreateRevision(IdKey key, Guid newRevisionId, Guid sourceRevisionId, RevisionType revisionType, Guid? cultureId)
        => new(NodeRevisionOperation.CreateRevision, key, newRevisionId, sourceRevisionId, null, revisionType, cultureId);

    private NodeRevisionAndMetaAction(NodeRevisionOperation operation, IdKey idKey, Guid revisionId, Guid? sourceRevisionId, INodeMeta? meta, RevisionType? revisionType, Guid? cultureId)
        : base(ActionTarget.NodeRevision) {
        Operation = operation;
        NodeIdKey = idKey;
        RevisionId = revisionId;
        SourceRevisionId = sourceRevisionId;
        RevisionType = revisionType;
        CultureId = cultureId;
        Meta = meta;
    }
    public NodeRevisionOperation Operation { get; }
    public IdKey NodeIdKey { get; }
    public INodeMeta? Meta { get; }
    public Guid RevisionId { get; }
    public Guid? SourceRevisionId { get; }
    public Guid? CultureId { get; }
    public RevisionType? RevisionType { get; }
    public override string ToString() => Operation.ToString().Decamelize();
    public override string OperationName() => "NodeRevisionAction." + Operation.ToString();
    public override string ToString(Datamodel dm) => ToString();
}
