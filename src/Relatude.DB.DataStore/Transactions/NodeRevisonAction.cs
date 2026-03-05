using Relatude.DB.Common;
using Relatude.DB.Datamodels;
namespace Relatude.DB.Transactions;

public enum NodeRevisionOperation : byte {
    UpdateMeta, // updates the meta data, all but revision type, and culture
    EnableRevisions, // enables a revision for the node
    DisableRevisions, // disables a revision for the node
    CreateRevision, // inserts or updates a revision for the node based on the node provided
    DeleteRevision, // deletes a revision for the node
    ChangeRevisionType, // changes the revision type of a revision, but keeps the same revision id
    ChangeRevisionCulture, // changes the culture of a revision, but keeps the same revision id
}
public class NodeRevisionAction : ActionBase {

    public static NodeRevisionAction UpdateMeta(IdKey key, Guid revisionId, NodeMeta? meta) // all but revision type and culture can be updated with this action
        => new(NodeRevisionOperation.UpdateMeta, key, revisionId, null, null, meta?.InnerMeta, null);
    public static NodeRevisionAction DeleteRevision(IdKey key, Guid revisionId)
        => new(NodeRevisionOperation.DeleteRevision, key, revisionId, null, null, null, null);
    public static NodeRevisionAction EnableRevisions(IdKey key, Guid? revisionId = null)
        => new(NodeRevisionOperation.EnableRevisions, key, revisionId, null, null, null, null);
    public static NodeRevisionAction DisableRevisions(IdKey key, Guid revisionIdToKeep)
        => new(NodeRevisionOperation.DisableRevisions, key, revisionIdToKeep, null, null, null, null);
    public static NodeRevisionAction CreateRevision(IdKey key, Guid sourceRevisionId, RevisionType revisionType, Guid? newRevisionId, Guid? cultureId)
        => new(NodeRevisionOperation.CreateRevision, key, newRevisionId ?? Guid.NewGuid(), revisionType, sourceRevisionId, null, cultureId);

    public static NodeRevisionAction ChangeRevisionType(IdKey key, Guid revisionId, RevisionType newRevisionType)
        => new(NodeRevisionOperation.ChangeRevisionType, key, revisionId, newRevisionType, null, null, null);
    public static NodeRevisionAction ChangeRevisionCulture(IdKey key, Guid revisionId, Guid newCultureId)
        => new(NodeRevisionOperation.ChangeRevisionCulture, key, revisionId, null, null, null, newCultureId);

    private NodeRevisionAction(NodeRevisionOperation operation, IdKey idKey, Guid? revisionId, RevisionType? revisionType, Guid? sourceRevisionId, INodeMeta? meta, Guid? cultureId)
        : base(ActionTarget.NodeRevision) {
        Operation = operation;
        NodeIdKey = idKey;
        RevisionId = revisionId;
        RevisionType = revisionType;
        SourceRevisionId = sourceRevisionId;
        CultureId = cultureId;
        Meta = meta;
    }
    public NodeRevisionOperation Operation { get; }
    public IdKey NodeIdKey { get; }
    public INodeMeta? Meta { get; }

    public Guid? RevisionId { get; }
    public Guid? CultureId { get; }

    public Guid? SourceRevisionId { get; }
    public Guid? SourceCultureId { get; }
    public RevisionType? RevisionType { get; }
    public override string ToString() => Operation.ToString().Decamelize(false);
    public override string OperationName() => "NodeRevisionAction." + Operation.ToString();
    public override string ToString(Datamodel dm) => ToString();
}
