using Relatude.DB.Common;
using Relatude.DB.Datamodels;

namespace Relatude.DB.Transactions;

public enum NodePropertyOperation : byte {
    Reset = 0, // reset the property to its default value, or remove it if it has no default value
    ForceUpdate, // always update the property, even if the value is the same
    UpdateIfDifferent, // [DEFAULT] update the property only if the value is different
    Add, // add the given value to the existing value, arethmetic for numeric properties, concatenation for strings, or union for sets
    Multiply, // multiply the existing value with the new value, for numeric properties only or strings that parse to numeric values.
}
public class NodePropertyAction : ActionBase {
    public NodePropertyAction(NodePropertyOperation operation, PropertyPath? propertyPath, object? value)
      : base(ActionTarget.NodeProperty) {
        Operation = operation;
        PropertyPath = propertyPath;
        Values = value == null ? null : [value];
        PropertyIds = [];
    }
    public NodePropertyAction(NodePropertyOperation operation, Guid? typeId, Guid[]? guids, int[]? nodeIds, Guid[]? revisionIds, Guid propertyId, object? value)
      : base(ActionTarget.NodeProperty) {
        Operation = operation;
        NodeGuids = guids;
        NodeIds = nodeIds;        
        RevisionIds = revisionIds;
        TypeId = typeId;
        PropertyIds = [propertyId];
        Values = value == null ? null : [value];
    }
    public NodePropertyAction(NodePropertyOperation operation, Guid? typeId, Guid[]? guids, int[]? nodeIds, Guid[]? revisionIds, Guid[] propertyIds, object[]? values)
      : base(ActionTarget.NodeProperty) {
        Operation = operation;
        NodeGuids = guids;
        NodeIds = nodeIds;
        RevisionIds = revisionIds;
        TypeId = typeId;
        PropertyIds = propertyIds;
        if (values != null && values.Length != propertyIds.Length) {
            throw new ArgumentException("Values length must match PropertyIds length.");
        }
        Values = values;
    }
    public NodePropertyOperation Operation { get; }
    public PropertyPath? PropertyPath { get; }
    public Guid? TypeId { get; }
    public Guid[]? NodeGuids { get; } = null;
    public Guid[]? RevisionIds { get; } = null;
    public int[]? NodeIds { get; } = null;
    public Guid[] PropertyIds { get; }
    public object[]? Values { get; }
    public override string ToString() {
        return Operation switch {
            NodePropertyOperation.Reset => "Reset node property. ",
            NodePropertyOperation.ForceUpdate => "Update node property. ",
            NodePropertyOperation.Add => "Add node property. ",
            NodePropertyOperation.Multiply => "Multiply node property. ",
            NodePropertyOperation.UpdateIfDifferent => "Ensure node property. ",
            _ => Operation.ToString(),
        };
    }
    public override string OperationName() => "NodePropertyAction." + Operation.ToString();
    public override string ToString(Datamodel dm) => ToString();
}
