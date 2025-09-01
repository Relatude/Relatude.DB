using Relatude.DB.Datamodels;
namespace Relatude.DB.Transactions;
public struct NodeSegment(long absolutePosition, int length) {
    public readonly long AbsolutePosition = absolutePosition;
    public readonly int Length = length;
}
public enum NodeOperation : byte {
    Insert, // insert a new node, fail if the node already exists
    InsertIfNotExists, // insert a new node, do nothing if the node already exists
    DeleteOrFail, // delete a node, fail if the node does not exist
    Delete, // delete a node, not fail if the node does not exist
    Update, // update a node, fail if the node does not exist, check if node is different before updating, faster if not changed (avoids disk writes), slower if changed due to unnecessary compare
    ForceUpdate, // update a node, fail if the node does not exist, update even if node is the same ( faster if changed as no compare, slower if not changed from disk writes
    Upsert, // insert a new node or update an existing one, check if node is different before updating, faster if not changed (avoids disk writes), slower if changed due to unnecessary compare
    ForceUpsert, // insert a new node or update an existing one, update even if node is the same ( faster if changed as no compare, slower if not changed from disk writes
    ChangeType, // change the type of a node, fail if node does not exist
    ReIndex, // triggers a re-index of the node, will not fail if the node does not exist
}
public class NodeAction : ActionBase {
    public static NodeAction Insert(INodeData node) => new(NodeOperation.Insert, node);
    public static NodeAction InsertIfNotExists(INodeData node) => new(NodeOperation.InsertIfNotExists, node);
    public static NodeAction Delete(int id) => new(NodeOperation.Delete, new NodeDataOnlyId(id));
    public static NodeAction Delete(Guid id) => new(NodeOperation.Delete, new NodeDataOnlyId(id));
    public static NodeAction DeleteOrFail(int id) => new(NodeOperation.DeleteOrFail, new NodeDataOnlyId(id));
    public static NodeAction DeleteOrFail(Guid id) => new(NodeOperation.DeleteOrFail, new NodeDataOnlyId(id));
    public static NodeAction Update(INodeData node) => new(NodeOperation.Update, node);
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
