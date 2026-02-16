using Relatude.DB.Native;

namespace Relatude.DB.Datamodels;

public struct NodeIdAndRevisionId {
    public Guid NodeId { get; }
    public Guid RevisionId { get; }
    public NodeIdAndRevisionId(Guid nodeId, Guid revisionId) {
        NodeId = nodeId;
        RevisionId = revisionId;
    }
}
public class QueryContext {
    public Guid UserId { get; private set; }
    public string? CultureCode { get; private set; }
    public bool IncludeDeleted { get; private set; } = false;
    public bool IncludeCultureFallback { get; private set; } = false;
    public bool IncludeUnpublished { get; private set; } = false;
    public bool EditView { get; private set; } = false;
    public bool IncludeHidden { get; private set; } = false;
    public bool ExcludeDecendants { get; private set; } = false;
    public DateTime? NowUtc { get; private set; }
    Guid[]? _collectionIds;
    public Guid[]? CollectionIds {
        get { return _collectionIds; }
        private set { _collectionIds = value?.OrderBy(id => id).ToArray(); } // ordered for equality comparison
    }
    public NodeIdAndRevisionId[]? SelectedRevisions { get; set; }
    public static readonly QueryContext Anonymous = new(
        ) {
        UserId = Guid.Empty,
        CultureCode = null,
        IncludeDeleted = false,
        IncludeCultureFallback = false,
        IncludeUnpublished = false,
        IncludeHidden = false,
        ExcludeDecendants = false,
        CollectionIds = null,
        NowUtc = null,
        SelectedRevisions = null
    };
    public static readonly QueryContext Default = new();
    public static readonly QueryContext AllExcludingDecendants = new() {
        UserId = NodeConstants.MasterAdminUserId,
        CultureCode = null,
        IncludeDeleted = true,
        IncludeCultureFallback = true,
        IncludeUnpublished = true,
        IncludeHidden = true,
        ExcludeDecendants = true,
        CollectionIds = null,
        NowUtc = null,
        SelectedRevisions = null

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
    static bool equalSelectedRevisions(NodeIdAndRevisionId[]? a, NodeIdAndRevisionId[]? b) {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) {
            if (a[i].NodeId != b[i].NodeId || a[i].RevisionId != b[i].RevisionId) return false;
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
            && NowUtc == other.NowUtc
            && equalSelectedRevisions(SelectedRevisions, other.SelectedRevisions);
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
        if (SelectedRevisions != null) {
            foreach (var revision in SelectedRevisions) {
                hash = (hash * 397) ^ revision.GetHashCode();
            }
        } else {
            hash = (hash * 397) ^ 0;
        }
        return hash;
    }
    QueryContext copy() {
        return new QueryContext {
            UserId = this.UserId,
            CultureCode = this.CultureCode,
            IncludeDeleted = this.IncludeDeleted,
            IncludeCultureFallback = this.IncludeCultureFallback,
            IncludeUnpublished = this.IncludeUnpublished,
            EditView = this.EditView,
            IncludeHidden = this.IncludeHidden,
            ExcludeDecendants = this.ExcludeDecendants,
            CollectionIds = this.CollectionIds,
            NowUtc = this.NowUtc,
            SelectedRevisions = this.SelectedRevisions
        };
    }
    public QueryContext User(string userName) {
        throw new NotImplementedException();
        //var userId = UserConstants.GetUserIdByName(userName);
        //return User(userId);
    }
    public QueryContext User(Guid userId) {
        if (this.UserId == userId) return this;
        var copy = this.copy();
        copy.UserId = userId;
        return copy;
    }
    public QueryContext Culture(string? cultureCode) {
        if (this.CultureCode == cultureCode) return this;
        var copy = this.copy();
        copy.CultureCode = cultureCode;
        return copy;
    }
    public QueryContext Admin() {
        if (this.UserId == NodeConstants.MasterAdminUserId) return this;
        var copy = this.copy();
        copy.UserId = NodeConstants.MasterAdminUserId;
        return copy;
    }
    public QueryContext Hidden(bool includeHidden = true) {
        if (this.IncludeHidden == includeHidden) return this;
        var copy = this.copy();
        copy.IncludeHidden = includeHidden;
        return copy;
    }
    public QueryContext Collections(Guid[]? collectionIds) {
        if (equalCollectionIds(this.CollectionIds, collectionIds)) return this;
        var copy = this.copy();
        copy.CollectionIds = collectionIds;
        return copy;
    }
    public QueryContext Now(DateTime? nowUtc) {
        if (this.NowUtc == nowUtc) return this;
        var copy = this.copy();
        copy.NowUtc = nowUtc;
        return copy;
    }
    public QueryContext EditViewMode(bool editView = true) {
        if (this.EditView == editView) return this;
        var copy = this.copy();
        copy.EditView = editView;
        return copy;
    }
    public QueryContext Unpublished(bool includeUnpublished = true) {
        if (this.IncludeUnpublished == includeUnpublished) return this;
        var copy = this.copy();
        copy.IncludeUnpublished = includeUnpublished;
        return copy;
    }
    public QueryContext Deleted(bool includeDeleted = true) {
        if (this.IncludeDeleted == includeDeleted) return this;
        var copy = this.copy();
        copy.IncludeDeleted = includeDeleted;
        return copy;        
    }
    public QueryContext Descendants(bool excludeDecendants = false) {
        if (this.ExcludeDecendants == excludeDecendants) return this;
        var copy = this.copy();
        copy.ExcludeDecendants = excludeDecendants;
        return copy;
    }
    public QueryContext CultureFallbacks(bool includeCultureFallback = true) {
        if (this.IncludeCultureFallback == includeCultureFallback) return this;
        var copy = this.copy();
        copy.IncludeCultureFallback = includeCultureFallback;
        return copy;
    }
    public override string ToString() {
        var s = $"User:{UserId} ";
        if (CultureCode != null) s += $"Culture:{CultureCode} ";
        if (IncludeDeleted) s += "Deleted ";
        if (IncludeCultureFallback) s += "CultureFallback ";
        if (IncludeUnpublished) s += "Unpublished ";
        if (EditView) s += "EditView ";
        if (IncludeHidden) s += "Hidden ";
        if (ExcludeDecendants) s += "NoDescendants ";
        if (CollectionIds != null) s += $"Collections:[{string.Join(",", CollectionIds)}] ";
        if (NowUtc != null) s += $"Now:{NowUtc} ";
        if (SelectedRevisions != null) s += $"SelectedRevisions:[{string.Join(",", SelectedRevisions.Select(r => $"{r.NodeId}:{r.RevisionId}"))}] ";
        return s;
    }
}
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
    public readonly NodeIdAndRevisionId[]? SelectedRevisions;
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
        SystemUserType userType,
        NodeIdAndRevisionId[]? selectedRevisions
        ) {
        CultureId = cultureId;
        CollectionIds = collectionIds;
        IncludeDeleted = includeDeleted;
        IncludeCultureFallback = includeCultureFallback;
        IncludeUnpublished = includeUnpublished;
        IncludeHidden = includeHidden;
        ExcludeDecendants = excludeDecendants;
        UserType = userType;
        SelectedRevisions = selectedRevisions;
    }
    int _hash = 0;
    public override int GetHashCode() {
        if (_hash != 0) return _hash;
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
        if (SelectedRevisions != null) {
            foreach (var id in SelectedRevisions) {
                hash = (hash * 397) ^ id.GetHashCode();
            }
        } else {
            hash = (hash * 397) ^ 0;
        }
        _hash = hash;
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
        if (IncludeDeleted) s += "Deleted ";
        if (IncludeCultureFallback) s += "CultureFallback ";
        if (IncludeUnpublished) s += "Unpublished ";
        if (EditView) s += "EditView ";
        if (IncludeHidden) s += "Hidden ";
        if (ExcludeDecendants) s += "NoDescendants ";
        s += $"Culture:{CultureId} ";
        if (IncludeUnpublished) s += $"UserType:{UserType} ";
        return s;
    }
}

