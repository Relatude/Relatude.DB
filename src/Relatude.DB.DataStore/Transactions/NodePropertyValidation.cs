using Relatude.DB.Common;
using Relatude.DB.Datamodels;

namespace Relatude.DB.Transactions;
public class NodePropertyValidation: ActionBase {
    public NodePropertyValidation(ValueRequirement operation, Guid[]? guids, int[]? nodeIds, Guid propertyId, object? value)
      : base(ActionTarget.NodeProperty) {
        Requirement = operation;
        NodeGuids = guids;
        NodeIds = nodeIds;
        PropertyId = propertyId;
        Value = value;
    }
    public ValueRequirement Requirement { get; }
    public Guid[]? NodeGuids { get; } = null;
    public int[]? NodeIds { get; } = null;
    public Guid PropertyId { get; }
    public object? Value { get; }
    public override string ToString() {
        return Requirement switch {
            ValueRequirement.Equal => "Validate node property equal. ",
            ValueRequirement.NotEqual => "Validate node property not equal. ",
            ValueRequirement.Less => "Validate node property less than. ",
            ValueRequirement.LessOrEqual => "Validate node property less than or equal. ",
            ValueRequirement.Greater => "Validate node property greater than. ",
            ValueRequirement.GreaterOrEqual => "Validate node property greater than or equal. ",
            _ => OperationName(),
        };
    }
    public override string OperationName() => "NodePropertyValidation." + Requirement.ToString();
    public override string ToString(Datamodel dm) => ToString();
}
