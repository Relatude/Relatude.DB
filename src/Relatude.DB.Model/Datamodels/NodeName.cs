namespace Relatude.DB.Datamodels;
/// <summary>
/// A node's name: the value of <c>IName.Name</c> (<see cref="NodeConstants.NamePropertyId"/>), the one property every
/// type that has a name shares. There is no other name and nothing is guessed: a node of a type without IName, or
/// with an empty name, has none, and whoever shows it says so ("Untitled").
/// </summary>
public static class NodeName {
    /// <summary>The name, or null for none. A node with revisions is asked of one of its revisions.</summary>
    public static string? Of(INodeData node) => node.TryGetValue(NodeConstants.NamePropertyId, out var value) && value is string s && !string.IsNullOrWhiteSpace(s) ? s : null;
}
