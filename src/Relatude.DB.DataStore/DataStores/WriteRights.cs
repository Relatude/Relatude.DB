namespace Relatude.DB.DataStores;

/// <summary>What a user may write of a node: change it at all (its edit group), and change what readers see of it (its publish group too).</summary>
public sealed record NodeWriteRights(bool Edit, bool Publish);

/// <summary>
/// What a user may write of nodes of a type: make them (the type's edit group), make them published (its publish group
/// too), and the properties they may give no value of their own (a property's write group, a user's type).
/// </summary>
public sealed record TypeWriteRights(bool Create, bool CreatePublished, Guid[] ReadOnlyProperties);
