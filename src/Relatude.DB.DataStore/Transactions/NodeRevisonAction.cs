using Relatude.DB.Common;
using Relatude.DB.Datamodels;
namespace Relatude.DB.Transactions;

public enum NodeRevisionOperation : byte {
    CreateRevision, // creates a new revision for the node, based on default values
    InsertRevision, // inserts a new revision for the node based on the node provided
    UpdateRevision, // updates an existing revision for the node based on the node provided
    DeleteRevision, // deletes a revision for the node
    SetRevisionState, // sets the state of a revision for the node
}
public class NodeRevisionAction : ActionBase {
    public static NodeRevisionAction CreateRevision(Guid nodeId, Guid revisionId, RevisionType state) => new(NodeRevisionOperation.CreateRevision, new IdKey(nodeId), revisionId, null, null);
    public static NodeRevisionAction InsertRevision(Guid nodeId, Guid revisionId, RevisionType state, INodeData node, string? cultureCode) => new(NodeRevisionOperation.InsertRevision, new IdKey(nodeId), revisionId, node, cultureCode);
    public static NodeRevisionAction DeleteRevision(Guid nodeId, Guid revisionId) => new(NodeRevisionOperation.DeleteRevision, new IdKey(nodeId), revisionId, null, null);
    public static NodeRevisionAction SetRevisionState(Guid nodeId, Guid revisionId, RevisionType state) => new(NodeRevisionOperation.SetRevisionState, new IdKey(nodeId), revisionId, null, null);
    public static NodeRevisionAction UpdateRevision(Guid nodeId, Guid revisionId, INodeData node, string? cultureCode) => new(NodeRevisionOperation.UpdateRevision, new IdKey(nodeId), revisionId, node, cultureCode);
    public static NodeRevisionAction CreateRevision(int nodeId, Guid revisionId, RevisionType state) => new(NodeRevisionOperation.CreateRevision, new IdKey(nodeId), revisionId, null, null);
    public static NodeRevisionAction InsertRevision(int nodeId, Guid revisionId, RevisionType state, INodeData node, string? cultureCode) => new(NodeRevisionOperation.InsertRevision, new IdKey(nodeId), revisionId, node, cultureCode);
    public static NodeRevisionAction DeleteRevision(int nodeId, Guid revisionId) => new(NodeRevisionOperation.DeleteRevision, new IdKey(nodeId), revisionId, null, null);
    public static NodeRevisionAction SetRevisionState(int nodeId, Guid revisionId, RevisionType state) => new(NodeRevisionOperation.SetRevisionState, new IdKey(nodeId), revisionId, null, null);
    public static NodeRevisionAction UpdateRevision(int nodeId, Guid revisionId, INodeData node, string? cultureCode) => new(NodeRevisionOperation.UpdateRevision, new IdKey(nodeId), revisionId, node, cultureCode);
    private NodeRevisionAction(NodeRevisionOperation operation, IdKey idKey, Guid revisionId, INodeData? node, string? cultureCode)
        : base(ActionTarget.NodeRevision) {
        Operation = operation;
        NodeIdKey = idKey;
        RevisionId = revisionId;
        Node = node;
    }
    public NodeRevisionOperation Operation { get; }
    public IdKey NodeIdKey;
    public Guid RevisionId { get; }
    public string? CultureCode { get; }
    public INodeData? Node { get; }
    public override string ToString() => Operation.ToString().Decamelize();
    public override string OperationName() => "NodeRevisionAction." + Operation.ToString();
    public override string ToString(Datamodel dm) => ToString();
}
