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
    public static NodeRevisionAction CreateRevision(Guid nodeId, Guid revisionId, RevisionType state) => new(NodeRevisionOperation.CreateRevision, nodeId, revisionId, null, null);
    public static NodeRevisionAction InsertRevision(Guid nodeId, Guid revisionId, RevisionType state, INodeData node, string? cultureCode) => new(NodeRevisionOperation.InsertRevision, nodeId, revisionId, node, cultureCode);
    public static NodeRevisionAction DeleteRevision(Guid nodeId, Guid revisionId) => new(NodeRevisionOperation.DeleteRevision, nodeId, revisionId, null, null);
    public static NodeRevisionAction SetRevisionState(Guid nodeId, Guid revisionId, RevisionType state) => new(NodeRevisionOperation.SetRevisionState, nodeId, revisionId, null, null);
    private NodeRevisionAction(NodeRevisionOperation operation, Guid nodeId, Guid revisionId, INodeData? node, string? cultureCode)
        : base(ActionTarget.NodeRevision) {
        Operation = operation;
        NodeId = nodeId;
        RevisionId = revisionId;
        Node = node;
    }
    public NodeRevisionOperation Operation { get; }
    public Guid NodeId { get; }
    public Guid RevisionId { get; }
    public string? CultureCode { get; }
    public INodeData? Node { get; }
    public override string ToString() => Operation.ToString().Decamelize();
    public override string OperationName() => "NodeRevisionAction." + Operation.ToString();
    public override string ToString(Datamodel dm) => ToString();
}
