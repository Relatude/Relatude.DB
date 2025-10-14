using Relatude.DB.Datamodels;
namespace Relatude.DB.Transactions;
public struct NodeSegment(long absolutePosition, int length) {
    public readonly long AbsolutePosition = absolutePosition;
    public readonly int Length = length;
}
public enum NodeOperation : byte {
    InsertOrFail, // [DEFAULT] inserts a new node, fails if a node with same ID already exists ( if ID is set )
    InsertIfNotExists, // inserts a new node, do nothing if a node with the ID already exists
    DeleteOrFail, // [DEFAULT] deletes a node, fails if the node does not exist
    DeleteIfExists, // deletes a node, ignored if the node does not exist
    UpdateIfExists, // updates a node, ignored if the node does not exist and only updates if changed, faster if not changed (avoids disk writes), slower if changed due to comparison
    UpdateOrFail, // [DEFAULT] updates a node, fails if the node does not exist
    ForceUpdate, // updates a node, fails if the node does not exist, but update even if not different ( faster if changed as no comparison, slower if not changed )
    Upsert, // inserts a new node or updates an existing one, checks if node is different before updating, faster if not changed (avoids disk writes), slower if changed due to unnecessary compare
    ForceUpsert, // inserts a new node or update an existing one, update even if node is the same  ( faster if changed as no comparison, slower if not changed )
    ChangeType, // changes the type of a node, fails if node does not exist
    ReIndex, // triggers a re-index of the node, ignored if the node does not exist
}
public class NodeAction : ActionBase {
    public static NodeAction InsertOrFail(INodeData node) => new(NodeOperation.InsertOrFail, node);
    public static NodeAction InsertIfNotExists(INodeData node) => new(NodeOperation.InsertIfNotExists, node);
    public static NodeAction DeleteIfExists(int id) => new(NodeOperation.DeleteIfExists, new NodeDataOnlyId(id));
    public static NodeAction DeleteIfExists(Guid id) => new(NodeOperation.DeleteIfExists, new NodeDataOnlyId(id));
    public static NodeAction DeleteOrFail(int id) => new(NodeOperation.DeleteOrFail, new NodeDataOnlyId(id));
    public static NodeAction DeleteOrFail(Guid id) => new(NodeOperation.DeleteOrFail, new NodeDataOnlyId(id));
    public static NodeAction UpdateIfExists(INodeData node) => new(NodeOperation.UpdateIfExists, node);
    public static NodeAction UpdateOrFail(INodeData node) => new(NodeOperation.UpdateOrFail, node);
    public static NodeAction ForceUpdate(INodeData node) => new(NodeOperation.ForceUpdate, node);
    public static NodeAction Upsert(INodeData node) => new(NodeOperation.Upsert, node);
    public static NodeAction ForceUpsert(INodeData node) => new(NodeOperation.ForceUpsert, node);
    public static NodeAction ChangeType(int id, Guid typeId) => new(NodeOperation.ChangeType, new NodeDataOnlyTypeAndUId(id, typeId));
    public static NodeAction ChangeType(Guid id, Guid typeId) => new(NodeOperation.ChangeType, new NodeDataOnlyTypeAndId(id, typeId));
    public static NodeAction ReIndex(int id) => new(NodeOperation.ReIndex, new NodeDataOnlyId(id));
    public static NodeAction ReIndex(Guid id) => new(NodeOperation.ReIndex, new NodeDataOnlyId(id));
    public static NodeAction Load(NodeOperation operation, INodeData node) => new NodeAction(operation, node);
    //public static NodeAction RemoveCulture(Guid id, int lcid) => new(NodeOperation.Update, NodeData.CreateEmptyDerivedNode(id, lcid)); // updating with a derived node will cause removal of culture
    private NodeAction(NodeOperation operation, INodeData node)
        : base(ActionTarget.Node) {
        Operation = operation; Node = node;
    }
    public NodeOperation Operation { get; set; }
    public INodeData Node { get; set; }
    public override string ToString() => OperationName() + " " + Node.ToString();
    public override string OperationName() => "NodeAction." + Operation.ToString();
    public override string ToString(Datamodel dm) => ToString();
}
