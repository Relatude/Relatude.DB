namespace Relatude.DB.Datamodels.Properties;
/// <summary>
/// What the ids a Guid or Guid[] property holds are ids of, so an editing interface can offer them by name rather than as
/// Guids. Presentation only: the store neither checks the ids nor follows them.
/// </summary>
public enum GuidValueType {
    /// <summary>Any id: an editing interface shows the Guid itself.</summary>
    AnyGuid,
    /// <summary>Node types of the datamodel.</summary>
    NodeType,
    /// <summary>Properties of the datamodel's node types.</summary>
    Property,
    /// <summary>Relations of the datamodel.</summary>
    Relation,
}
