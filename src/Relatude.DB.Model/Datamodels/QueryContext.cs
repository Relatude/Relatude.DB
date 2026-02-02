using Relatude.DB.Native;

namespace Relatude.DB.Datamodels;

public class QueryContext {
    public Guid UserId { get; set; }
    public string? CultureCode { get; set; }
    public bool IncludeDeleted { get; set; } = false;
    public bool IncludeCultureFallback { get; set; } = false;
    public bool IncludeUnpublished { get; set; } = false;
    public bool EditView { get; set; } = false;
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
        // slower but better distributed:
        //var hash = new HashCode();
        //hash.Add(UserId);
        //hash.Add(CultureCode);
        //hash.Add(IncludeDeleted);
        //hash.Add(IncludeCultureFallback);
        //hash.Add(IncludeUnpublished);
        //hash.Add(IncludeHidden);
        //hash.Add(ExcludeDecendants);
        //if (CollectionIds != null) {
        //    foreach (var id in CollectionIds) {
        //        hash.Add(id);
        //    }
        //} else {
        //    hash.Add(0);
        //}
        //hash.Add(NowUtc);
        //return hash.ToHashCode();

        // faster but less distributed:
        int hash = 17;
        hash = (hash * 397) ^ UserId.GetHashCode();
        hash = (hash * 397) ^ (CultureCode?.GetHashCode() ?? 0);
        hash = (hash * 397) ^ IncludeDeleted.GetHashCode();
        hash = (hash * 397) ^ IncludeCultureFallback.GetHashCode();
        hash = (hash * 397) ^ IncludeUnpublished.GetHashCode();
        hash = (hash * 397) ^ IncludeHidden.GetHashCode();
        hash = (hash * 397) ^ ExcludeDecendants.GetHashCode();
        if (CollectionIds != null) {
            foreach (var id in CollectionIds) {
                hash = (hash * 397) ^ id.GetHashCode();
            }
        } else {
            hash = (hash * 397) ^ 0;
        }
        hash = (hash * 397) ^ (NowUtc?.GetHashCode() ?? 0);
        return hash;
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
    public readonly bool EditView;
    public readonly bool IncludeHidden;
    public readonly bool ExcludeDecendants;
    public readonly Guid CultureId;
    public readonly Guid[]? CollectionIds;
    public readonly Guid[]? MembershipIds;
    public bool IsMember(Guid groupId) {
        if (groupId == Guid.Empty) return true;
        if (MembershipIds == null) return false;
        if (MembershipIds.Length == 0) return false;
        foreach (var id in MembershipIds) {
            if (id == groupId) return true;
        }
        return false;
    }
    public readonly SystemUserType UserType;
    public QueryContextKey(
        Guid cultureId,
        Guid[]? collectionIds,
        Guid[]? membershipIds,
        bool includeDeleted,
        bool includeCultureFallback,
        bool includeUnpublished,
        bool editView,
        bool includeHidden,
        bool excludeDecendants,
        SystemUserType userType
        ) {
        CultureId = cultureId;
        CollectionIds = collectionIds;
        IncludeDeleted = includeDeleted;
        IncludeCultureFallback = includeCultureFallback;
        IncludeUnpublished = includeUnpublished;
        IncludeHidden = includeHidden;
        ExcludeDecendants = excludeDecendants;
        UserType = userType;
    }
    public override int GetHashCode() {

        // slower but better distributed:
        //var hash = new HashCode();
        //hash.Add(CultureId);
        //hash.Add(IncludeDeleted);
        //hash.Add(IncludeCultureFallback);
        //hash.Add(IncludeUnpublished);
        //hash.Add(EditView);
        //hash.Add(IncludeHidden);
        //hash.Add(ExcludeDecendants);
        //hach.Add(UserType);
        //if (CollectionIds != null) {
        //    foreach (var id in CollectionIds) {
        //        hash.Add(id);
        //    }
        //} else {
        //    hash.Add(0);
        //}
        //if (MembershipIds != null) {
        //    foreach (var id in MembershipIds) {
        //        hash.Add(id);
        //    }
        //} else {
        //    hash.Add(0);
        //}
        //return hash.ToHashCode();

        // faster but less distributed:
        int hash = CultureId.GetHashCode();
        hash = (hash * 397) ^ IncludeDeleted.GetHashCode();
        hash = (hash * 397) ^ IncludeCultureFallback.GetHashCode();
        hash = (hash * 397) ^ IncludeUnpublished.GetHashCode();
        hash = (hash * 397) ^ EditView.GetHashCode();
        hash = (hash * 397) ^ IncludeHidden.GetHashCode();
        hash = (hash * 397) ^ ExcludeDecendants.GetHashCode();
        hash = (hash * 397) ^ UserType.GetHashCode();
        if (CollectionIds != null) {
            foreach (var id in CollectionIds) {
                hash = (hash * 397) ^ id.GetHashCode();
            }
        } else {
            hash = (hash * 397) ^ 0;
        }
        if (MembershipIds != null) {
            foreach (var id in MembershipIds) {
                hash = (hash * 397) ^ id.GetHashCode();
            }
        } else {
            hash = (hash * 397) ^ 0;
        }
        return hash;


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
            && EditView == other.EditView
            && IncludeHidden == other.IncludeHidden
            && ExcludeDecendants == other.ExcludeDecendants
            && UserType == other.UserType
            && equalIds(CollectionIds, other.CollectionIds)
            && equalIds(MembershipIds, other.MembershipIds);
    }
    static bool equalIds(Guid[]? a, Guid[]? b) {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) {
            if (a[i] != b[i]) return false;
        }
        return true;
    }
    public override string ToString() {
        var s = "";
        if(IncludeDeleted) s += "Deleted ";
        if(IncludeCultureFallback) s += "CultureFallback ";
        if(IncludeUnpublished) s += "Unpublished ";
        if(EditView) s += "EditView ";
        if(IncludeHidden) s += "Hidden ";
        if(ExcludeDecendants) s += "NoDescendants ";
        s += $"Culture:{CultureId} ";
        if(IncludeUnpublished) s += $"UserType:{UserType} ";
        return s;
    }
}

