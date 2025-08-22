using WAF.Datamodels;

namespace WAF.Transactions;
public enum NodePropertyOperation : byte {
    Reset = 0,
    Update,
    UpdateIfDifferent,
    Add,
    Multiply,
}
public class NodePropertyAction : ActionBase {
    public NodePropertyAction(NodePropertyOperation operation, Guid? typeId, Guid[]? guids, int[]? nodeIds, Guid propertyId, object? value)
      : base(ActionTarget.NodeProperty) {
        Operation = operation;
        NodeGuids = guids;
        NodeIds = nodeIds;
        TypeId = typeId;
        PropertyIds = [propertyId];
        Values = value == null ? null : [value];
    }
    public NodePropertyAction(NodePropertyOperation operation, Guid? typeId, Guid[]? guids, int[]? nodeIds, Guid[] propertyIds, object[]? values)
      : base(ActionTarget.NodeProperty) {
        Operation = operation;
        NodeGuids = guids;
        NodeIds = nodeIds;
        TypeId = typeId;
        PropertyIds = propertyIds;
        if (values != null && values.Length != propertyIds.Length) {
            throw new ArgumentException("Values length must match PropertyIds length.");
        }
        Values = values;
    }
    public NodePropertyOperation Operation { get; }
    public Guid? TypeId { get; }
    public Guid[]? NodeGuids { get; } = null;
    public int[]? NodeIds { get; } = null;
    public Guid[] PropertyIds { get; }
    public object[]? Values { get; }
    public override string ToString() {
        return Operation switch {
            NodePropertyOperation.Reset => "Reset node property. ",
            NodePropertyOperation.Update => "Update node property. ",
            NodePropertyOperation.Add => "Add node property. ",
            NodePropertyOperation.Multiply => "Multiply node property. ",
            NodePropertyOperation.UpdateIfDifferent => "Ensure node property. ",
            _ => Operation.ToString(),
        };
    }
    public override string OperationName() => "NodePropertyAction." + Operation.ToString();
    public override string ToString(Datamodel dm) => ToString();
}
