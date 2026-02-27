using Relatude.DB.Common;
using Relatude.DB.Datamodels;
namespace Relatude.DB.Transactions;

public enum NodeRevisionOperation : byte {
    UpdateMeta, // updates the meta data
    CreateRevision, // inserts or updates a revision for the node based on the node provided
    DeleteRevision, // deletes a revision for the node
}
public class NodeRevisionAction : ActionBase {

    public static NodeRevisionAction UpdateMeta(IdKey key, Guid revisionId, INodeMeta meta) => new(NodeRevisionOperation.UpdateMeta, key, revisionId, meta);
    public static NodeRevisionAction DeleteRevision(IdKey key, Guid revisionId) => new(NodeRevisionOperation.DeleteRevision, key, revisionId);
    public static NodeRevisionAction InsertRevision(IdKey key, Guid revisionId, INodeMeta meta) => new(NodeRevisionOperation.CreateRevision, new IdKey(node.Id, node.__Id), revisionId, null);

    private NodeRevisionAction(NodeRevisionOperation operation, IdKey idKey, Guid revisionId, INodeMeta? meta = null)
        : base(ActionTarget.NodeRevision) {
        Operation = operation;
        NodeIdKey = idKey;
        RevisionId = revisionId;
        Meta = meta;    
    }
    public NodeRevisionOperation Operation { get; }
    public IdKey NodeIdKey { get; }
    public INodeMeta? Meta { get; }
    public Guid RevisionId { get; }
    public override string ToString() => Operation.ToString().Decamelize();
    public override string OperationName() => "NodeRevisionAction." + Operation.ToString();
    public override string ToString(Datamodel dm) => ToString();
}
