using Relatude.DB.Common;
using Relatude.DB.Datamodels;
namespace Relatude.DB.Transactions;

public enum NodeRevisionOperation : byte {
    UpdateMeta, // updates the meta data
    UpsertRevision, // inserts or updates a revision for the node based on the node provided
    DeleteRevision, // deletes a revision for the node
}
public class NodeRevisionAction : ActionBase {

    public static NodeRevisionAction UpdateMeta(IdKey key, Guid revisionId, INodeMeta meta) => new(NodeRevisionOperation.UpdateMeta, key, revisionId, null, null, meta);
    public static NodeRevisionAction DeleteRevision(IdKey key, Guid revisionId) => new(NodeRevisionOperation.DeleteRevision, key, revisionId, null, null);
    public static NodeRevisionAction UpsertRevision(INodeData node, Guid revisionId) => new(NodeRevisionOperation.UpsertRevision, new IdKey(node.Id, node.__Id), revisionId, node, null);

    private NodeRevisionAction(NodeRevisionOperation operation, IdKey idKey, Guid revisionId, INodeData? node, string? cultureCode, INodeMeta? meta = null)
        : base(ActionTarget.NodeRevision) {
        Operation = operation;
        NodeIdKey = idKey;
        RevisionId = revisionId;
        Node = node;
        CultureCode = cultureCode;
        Meta = meta;    
    }
    public NodeRevisionOperation Operation { get; }
    public IdKey NodeIdKey { get; }
    public INodeMeta? Meta { get; }
    public Guid RevisionId { get; }
    public string? CultureCode { get; }
    public INodeData? Node { get; }
    public override string ToString() => Operation.ToString().Decamelize();
    public override string OperationName() => "NodeRevisionAction." + Operation.ToString();
    public override string ToString(Datamodel dm) => ToString();
}
