using System.Runtime.CompilerServices;

namespace Relatude.DB.Datamodels;

public class QueryContext {
    public Guid UserId { get; set; }
    public string? CultureCode { get; set; }
    public bool IncludeDeleted { get; set; } = false;
    public bool IncludeCultureFallback { get; set; } = false;
    public bool IncludeUnpublished { get; set; } = false;
    public bool IncludeHidden { get; set; } = false;
    public bool ExcludeDecendants { get; set; } = false;
    public DateTime? NowUtc;
    Guid[]? _collectionIds;
    public Guid[]? CollectionIds {
        get { return _collectionIds; }
        set { _collectionIds = value?.OrderBy(id => id).ToArray(); } // ordered for equality comparison
    }
    //public IRevisionSwitcher? RevisionSwitcher { get; set; }
    public static QueryContext CreateDefault() {
        return new QueryContext();
    }
    public static readonly QueryContext AllExcludingDecendants = new() {
        UserId = NodeConstants.MasterAdminUserId,
        CultureCode = null,
        IncludeDeleted = true,
        IncludeCultureFallback = true,
        IncludeUnpublished = true,
        IncludeHidden = true,
        ExcludeDecendants = true,
        CollectionIds = null,
        NowUtc = null
    };
    public static QueryContext AllIncludingDescendants = new() {
        UserId = NodeConstants.MasterAdminUserId,
        CultureCode = null,
        IncludeDeleted = true,
        IncludeCultureFallback = true,
        IncludeUnpublished = true,
        IncludeHidden = true,
        ExcludeDecendants = false,
        CollectionIds = null,
        NowUtc = null
    };
    static bool equalCollectionIds(Guid[]? a, Guid[]? b) {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) {
            if (a[i] != b[i]) return false;
        }
        return true;
    }
    public override bool Equals(object? obj) {
        if (obj is not QueryContext other) return false;
        return UserId == other.UserId
            && CultureCode == other.CultureCode
            && IncludeDeleted == other.IncludeDeleted
            && IncludeCultureFallback == other.IncludeCultureFallback
            && IncludeUnpublished == other.IncludeUnpublished
            && IncludeHidden == other.IncludeHidden
            && ExcludeDecendants == other.ExcludeDecendants
            && equalCollectionIds(CollectionIds, other.CollectionIds)
            && NowUtc == other.NowUtc;
    }
    public override int GetHashCode() {
        return HashCode.Combine(
            HashCode.Combine(
                UserId,
                CultureCode,
                IncludeDeleted,
                IncludeCultureFallback,
                IncludeUnpublished,
                IncludeHidden,
                ExcludeDecendants
            ),
            HashCode.Combine(
                CollectionIds != null ? CollectionIds.Aggregate(0, (acc, id) => acc ^ id.GetHashCode()) : 0
                , NowUtc
            )
        );
    }
}
//public interface IRevisionSwitcher {
//    NodeData RevisionSwitcher(NodeData selected, NodeData[] allRevisions);
//}
//public class PreviewSelector(Guid nodeId, int revisionId) : IRevisionSwitcher {
//    public NodeData RevisionSwitcher(NodeData selected, NodeData[] allRevisions) {
//        if (selected.Id == nodeId) {
//            foreach (var r in allRevisions) {
//                //if (r.RevisionId == revisionId) return r;
//            }
//        }
//        return selected;
//    }
//}
//public class NodeMeta {

//    public Guid ReadAccessId { get; set; } // hard read access for nodes in any context
//    public Guid EditViewAccessId { get; set; } // soft filter for to show or hide nodes in the edit ui
//    public Guid EditWriteAccessId { get; set; } // control access to edit unpublished revisions and request publication/depublication
//    public Guid PublishAccessId { get; set; } // control access to change live publish or depublish revisions
//    public string? CultureCode { get; }
//    public bool IsFallbackCulture { get; set; }

//    public int RevisionId { get; set; }
//    public bool IsDeleted { get; set; }

//    public DateTime ChangedUtc { get; }
//    public DateTime CreatedUtc { get; set; }
//    public DateTime PublishedUtc { get; }
//    public DateTime RetainedUtc { get; set; }
//    public DateTime ReleasedUtc { get; set; }

//}

public class QueryContextKey : IEquatable<QueryContextKey> {
    public readonly bool IncludeDeleted;
    public readonly bool IncludeCultureFallback;  // requires evaluating multiple versions
    public readonly bool IncludeUnpublished;  // requires evaluating multiple versions
    public readonly bool IncludeHidden;
    public readonly bool ExcludeDecendants;
    public readonly int CultureId;
    public readonly int[]? CollectionIds;
    public readonly int[]? MembershipIds;
    public QueryContextKey(int cultureId, int[]? collectionIds, int[]? membershipIds, bool includeDeleted, bool includeCultureFallback, bool includeUnpublished, bool includeHidden, bool excludeDecendants
        ) {
        CultureId = cultureId;
        CollectionIds = collectionIds;
        IncludeDeleted = includeDeleted;
        IncludeCultureFallback = includeCultureFallback;
        IncludeUnpublished = includeUnpublished;
        IncludeHidden = includeHidden;
        ExcludeDecendants = excludeDecendants;
    }
    public override int GetHashCode() {
        return HashCode.Combine(
            CultureId,
            IncludeDeleted,
            IncludeCultureFallback,
            IncludeUnpublished,
            IncludeHidden,
            ExcludeDecendants,
            CollectionIds != null ?
                CollectionIds.Aggregate(0, (acc, id) => acc ^ id.GetHashCode())
                : 0,
            MembershipIds != null ?
                MembershipIds.Aggregate(0, (acc, id) => acc ^ id.GetHashCode())
                : 0
        );
    }
    public override bool Equals(object? obj) {
        return obj is QueryContextKey other && Equals(other);
    }
    public bool Equals(QueryContextKey? other) {
        if (other == null) return false;
        return CultureId == other.CultureId
            && IncludeDeleted == other.IncludeDeleted
            && IncludeCultureFallback == other.IncludeCultureFallback
            && IncludeUnpublished == other.IncludeUnpublished
            && IncludeHidden == other.IncludeHidden
            && ExcludeDecendants == other.ExcludeDecendants
            && equalIds(CollectionIds, other.CollectionIds)
            && equalIds(MembershipIds, other.MembershipIds);
    }
    static bool equalIds(int[]? a, int[]? b) {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) {
            if (a[i] != b[i]) return false;
        }
        return true;
    }
}



