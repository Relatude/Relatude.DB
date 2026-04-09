using Relatude.DB.Common;
using Relatude.DB.Datamodels;

namespace Relatude.DB.Transactions;
public enum NodeAddressOperation : byte {
    Reset = 0,
    UpdateOrFail = 1,
    UpdateOrFallback = 3,
    SetAutomatic = 4,
    SetManual = 5,
}
public class NodeAddressAction : ActionBase {
    public NodeAddressAction(IdKey idKey, Guid? revisionId, NodeAddressOperation operation, string? address = null)
      : base(ActionTarget.NodeAddress) {
        IdKey = idKey;
        RevisionId = revisionId;
        Operation = operation;
        Address = address;
    }
    public NodeAddressOperation Operation { get; }
    public IdKey IdKey { get; }
    public Guid? RevisionId { get; }
    public string? Address;
    public override string ToString() {
        return Operation switch {
            NodeAddressOperation.Reset => "Reset node address. ",
            NodeAddressOperation.UpdateOrFail => "Update node address. ",
            NodeAddressOperation.UpdateOrFallback => "Update node address if different. ",
            NodeAddressOperation.SetAutomatic => "Set node address to automatic. ",
            NodeAddressOperation.SetManual => "Set node address to manual. ",
            _ => Operation.ToString(),
        };
    }
    public override string OperationName() => "NodeAddressAction." + Operation.ToString();
    public override string ToString(Datamodel dm) => ToString();
}
