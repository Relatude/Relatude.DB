namespace Relatude.DB.Datamodels.Properties;
/// <summary>How an editing interface shows a property that holds other nodes (a relation end, a reference or references, an embedded list). It says nothing to the store.</summary>
public enum DisplayMode {
    /// <summary>A list of the nodes, as an editor shows them when nothing else is said.</summary>
    List,
    /// <summary>Rows with columns, paged.</summary>
    Table,
    /// <summary>Each node as a form of its own that opens in place.</summary>
    Inline,
    /// <summary>A card of information for each node.</summary>
    Cards,
    /// <summary>The type's icon and the node's first picture.</summary>
    Thumbnail,
}
