namespace Relatude.DB.Datamodels;
/// <summary>
/// Direction of a graph traversal over a relation, relative to the relation property used.
/// </summary>
public enum GraphDirection : int {
    /// <summary>Follow the relation in the direction of the property. </summary>
    Default = 0,
    /// <summary>Follow the relation in the opposite direction of the property. </summary>
    Reverse = 1,
    /// <summary>
    /// Treat the relation as undirected, following edges both ways.
    /// Only supported for relations where source and target types overlap (self relations).
    /// Symmetric relations (OneOne, ManyMany) always behave as Both.
    /// </summary>
    Both = 2,
}
