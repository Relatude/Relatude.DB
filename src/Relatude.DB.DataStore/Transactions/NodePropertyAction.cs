using Relatude.DB.Common;
using Relatude.DB.Datamodels;

namespace Relatude.DB.Transactions;

public enum NodePropertyOperation : byte {
    Reset = 0, // reset the property to its default value, or remove it if it has no default value
    ForceUpdate, // always update the property, even if the value is the same
    UpdateIfDifferent, // [DEFAULT] update the property only if the value is different
    Add, // add the given value to the existing value, arethmetic for numeric properties, concatenation for strings, or union for sets
    Multiply, // multiply the existing value with the new value, for numeric properties only or strings that parse to numeric values.
    CopyFile, // give a file property the file of another (NodeFileCopyAction), which the store resolves before the transaction runs
    SetAddresses, // give many nodes their addresses, checked for unique URLs together (NodeAddressesAction)
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
            NodePropertyOperation.CopyFile => "Copy file. ",
            NodePropertyOperation.SetAddresses => "Set addresses. ",
            _ => Operation.ToString(),
        };
    }
    public override string OperationName() => "NodePropertyAction." + Operation.ToString();
    public override string ToString(Datamodel dm) => ToString();
}

/// <summary>
/// Gives a file property of a node the file of another file property (TransactionData.CopyFile): its name and what
/// was read from it, and the stored file itself where the target's store keeps one copy per content
/// (SameHashSameFile), else a copy of the bytes in the target's store. The store resolves it before the transaction
/// takes its lock, so no file is read or written under it, and writes the value as its own: one written from outside
/// may not name a stored file. A transaction plugin sees it as a property action with the operation CopyFile and no
/// values. The source must be stored already, the target may be inserted earlier in the same transaction.
/// </summary>
public sealed class NodeFileCopyAction : NodePropertyAction {
    public NodeFileCopyAction(PropertyPath from, PropertyPath to)
      : base(NodePropertyOperation.CopyFile, null,
            to.NodePath.NodeKey.HasGuid ? [to.NodePath.NodeKey.Guid] : null,
            to.NodePath.NodeKey.HasGuid ? null : [to.NodePath.NodeKey.Int],
            null, to.PropertyId, null) {
        if (to.NodePath.Path.Length > 0) throw new ArgumentException("A file is copied to a file property of a node, not of a node held in a property. ");
        From = from;
        To = to;
    }
    public PropertyPath From { get; }
    public PropertyPath To { get; }
    /// <summary>The value the target got, once the transaction has run: null before, and when the source held no file
    /// or it could not be read, which leaves the target as it was.</summary>
    public FileValue? Result { get; internal set; }
    // what the store resolved before the transaction: the target's value, and whether its bytes were stored for it
    internal bool IsResolved { get; set; }
    internal FileValue? Resolved { get; set; }
    internal bool Stored { get; set; }
    public override string ToString() => "Copy file. ";
}

/// <summary>
/// Gives many nodes their addresses (TransactionData.SetAddresses), each checked for a unique URL as any address is,
/// with what is left unchanged when null, in the order given. Where the URL manager allows it (IUrlManagerBatch) it
/// costs less than one write at a time: a node whose parent this action has already given its address is compared
/// with that parent's other children alone, since the parent's URL, just checked, is unique, and only a node under
/// the same parent can then have the node's URL; and what the checks read is read once for the action, the nodes
/// holding each address among it. So the nodes of a branch go top first, parents before their children, and a copied
/// branch costs one full check, for its top, however many nodes share its addresses. A transaction plugin sees it as
/// a property action on each node, on the address property, with the operation SetAddresses and no values.
/// </summary>
public sealed class NodeAddressesAction : NodePropertyAction {
    public NodeAddressesAction(IReadOnlyList<(Guid Node, string? Address, bool? AutoAddress)> nodes)
      : base(NodePropertyOperation.SetAddresses, null, [.. nodes.Select(n => n.Node)], null, null, NodeConstants.SystemAddressPropertyId, null) {
        Nodes = nodes;
    }
    /// <summary>The nodes, with the address each gets and whether the store keeps it in step with the node's name.</summary>
    public IReadOnlyList<(Guid Node, string? Address, bool? AutoAddress)> Nodes { get; }
    public override string ToString() => "Set addresses. ";
}
