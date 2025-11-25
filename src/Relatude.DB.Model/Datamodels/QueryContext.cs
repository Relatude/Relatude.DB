using Relatude.DB.Datamodels;

namespace Relatude.DB.AccessControl;

public class QueryContext {
    public Guid UserId { get; set; }
    public string? CultureCode { get; set; }
    public bool IncludeDeleted { get; set; } = false;
    public bool IncludeCultureFallback { get; set; } = false;
    public bool IncludeUnpublished { get; set; } = false;
    public bool IncludeHidden { get; set; } = false;
    public bool AnyCollections { get; set; } = false;
    public IRevisionSwitcher? RevisionSwitcher { get; set; }
}
public interface IRevisionSwitcher {
    NodeData RevisionSwitcher(NodeData selected, NodeData[] allRevisions);
}
public class PreviewSelector(Guid nodeId, int revisionId) : IRevisionSwitcher {
    public NodeData RevisionSwitcher(NodeData selected, NodeData[] allRevisions) {
        if (selected.Id == nodeId) {
            foreach (var r in allRevisions) {
                if (r.RevisionId == revisionId) return r;
            }
        }
        return selected;
    }
}
public class NodeMeta {
    public Guid ReadAccessId { get; set; } // hard read access for nodes in any context
    public Guid EditViewAccessId { get; set; } // soft filter for to show or hide nodes in the edit ui
    public Guid EditWriteAccessId { get; set; } // control access to edit unpublished revisions and request publication/depublication
    public Guid PublishAccessId { get; set; } // control access to change live publish or depublish revisions
    public int RevisionId { get; set; }
    public bool IsDeleted { get; set; }
    public string? CultureCode { get; }
    public bool IsFallbackCulture { get; set; }
}
