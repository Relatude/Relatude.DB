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
    public bool AnyCollections { get; set; } = false;
    public IRevisionSwitcher? RevisionSwitcher { get; set; }
    public static QueryContext CreateDefault() {
        return new QueryContext();
    }
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
    public string? CultureCode { get; }
    public bool IsFallbackCulture { get; set; }

    public int RevisionId { get; set; }
    public bool IsDeleted { get; set; }

    public DateTime ChangedUtc { get; }
    public DateTime CreatedUtc { get; set; }
    public DateTime PublishedUtc { get; }
    public DateTime RetainedUtc { get; set; }
    public DateTime ReleasedUtc { get; set; }


}

/// <summary>
/// Represents a high-performance, immutable cache key.
/// Declared as 'readonly struct' to eliminate defensive copies and GC pressure.
/// </summary>
public readonly struct NodeCacheKey :
    IComparable<NodeCacheKey>,
    IEquatable<NodeCacheKey> {
    // Make all fields 'readonly' to ensure immutability, which is enforced by the 'readonly struct' keyword.
    // Ordering fields by expected uniqueness helps Comparison methods short-circuit faster.

    // Most Discriminative
    public readonly Guid NodeType;

    // Core IDs
    public readonly int CollectionId;
    public readonly int CultureId;

    // Access and User IDs
    public readonly int ReadAccess;
    public readonly int EditViewAccess;
    public readonly int PublishAccess;
    public readonly int CreatedBy;
    public readonly int ChangedBy;

    // Booleans
    public readonly bool IsPublished;
    public readonly bool IsDeleted;

    // DateTimes (Get-only properties are implicitly readonly)
    public DateTime ChangedUtc { get; }
    public DateTime CreatedUtc { get; }
    public DateTime PublishedUtc { get; }
    public DateTime RetainedUtc { get; }
    public DateTime ReleasedUtc { get; }

    /// <summary>
    /// Constructor: The only way to set the values of a readonly struct.
    /// </summary>
    public NodeCacheKey(
        Guid nodeType, int readAccess, int editViewAccess, int publishAccess,
        int createdBy, int changedBy, int cultureId, int collectionId,
        bool isPublished, bool isDeleted,
        DateTime changedUtc, DateTime createdUtc, DateTime publishedUtc,
        DateTime retainedUtc, DateTime releasedUtc) {
        NodeType = nodeType;
        ReadAccess = readAccess;
        EditViewAccess = editViewAccess;
        PublishAccess = publishAccess;
        CreatedBy = createdBy;
        ChangedBy = changedBy;
        CultureId = cultureId;
        CollectionId = collectionId;
        IsPublished = isPublished;
        IsDeleted = isDeleted;
        ChangedUtc = changedUtc;
        CreatedUtc = createdUtc;
        PublishedUtc = publishedUtc;
        RetainedUtc = retainedUtc;
        ReleasedUtc = releasedUtc;
    }

    public NodeCacheKey(Guid TypeId, QueryContext ctx) { 
        NodeType = TypeId;
        IsPublished = !ctx.IncludeUnpublished;
        IsDeleted = !ctx.IncludeDeleted;
        ChangedUtc = DateTime.MinValue;
        CreatedUtc = DateTime.MinValue;
        PublishedUtc = DateTime.MinValue;
        RetainedUtc = DateTime.MinValue;
        ReleasedUtc = DateTime.MinValue;
    }

    // --- 1. Equality (IEquatable<T>) ---

    /// <summary>
    /// Implements strongly-typed, high-speed equality check.
    /// Uses the 'in' modifier to avoid copying the entire struct value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] // Ask the JIT to inline the code
    public bool Equals(in NodeCacheKey other) {
        // Direct comparison of primitive types is the fastest method.
        // Order must match the field order for consistency.

        if (NodeType != other.NodeType) return false;
        if (CollectionId != other.CollectionId) return false;
        if (CultureId != other.CultureId) return false;

        if (ReadAccess != other.ReadAccess) return false;
        if (EditViewAccess != other.EditViewAccess) return false;
        if (PublishAccess != other.PublishAccess) return false;
        if (CreatedBy != other.CreatedBy) return false;
        if (ChangedBy != other.ChangedBy) return false;

        if (IsPublished != other.IsPublished) return false;
        if (IsDeleted != other.IsDeleted) return false;

        // DateTime comparison is slightly slower, so it is placed last.
        if (ChangedUtc != other.ChangedUtc) return false;
        if (CreatedUtc != other.CreatedUtc) return false;
        if (PublishedUtc != other.PublishedUtc) return false;
        if (RetainedUtc != other.RetainedUtc) return false;

        return ReleasedUtc == other.ReleasedUtc;
    }

    /// <summary>
    /// Required implementation of IEquatable<T>.
    /// </summary>
    public bool Equals(NodeCacheKey other) => Equals(in other);

    /// <summary>
    /// Overrides Object.Equals, safely delegating to the faster strongly-typed Equals.
    /// </summary>
    public override bool Equals(object? obj) => obj is NodeCacheKey other && Equals(in other);

    // --- 2. Hashing ---

    /// <summary>
    /// Generates a fast, low-collision hash code using modern .NET best practices.
    /// </summary>
    public override int GetHashCode() {
        // HashCode.Combine is the most efficient and robust way to combine many values.
        // It takes up to 8 arguments in a single call, so we use two calls 
        // and add the results for maximum performance and collision resistance.

        return HashCode.Combine(
            NodeType, CollectionId, CultureId,
            ReadAccess, EditViewAccess, PublishAccess, CreatedBy, ChangedBy)
        + HashCode.Combine(
            IsPublished, IsDeleted,
            ChangedUtc, CreatedUtc, PublishedUtc, RetainedUtc, ReleasedUtc);
    }

    // --- 3. Comparison (IComparable<T>) ---

    /// <summary>
    /// Implements strongly-typed, high-speed comparison for sorting or ordering.
    /// Uses the 'in' modifier to avoid copying the entire struct value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int CompareTo(in NodeCacheKey other) {
        // Use integer result variable for performance and early exit (short-circuiting).
        int result;

        // 1. GUID
        result = NodeType.CompareTo(other.NodeType);
        if (result != 0) return result;

        // 2. Core IDs
        result = CollectionId.CompareTo(other.CollectionId);
        if (result != 0) return result;
        result = CultureId.CompareTo(other.CultureId);
        if (result != 0) return result;

        // 3. Access/User IDs
        result = ReadAccess.CompareTo(other.ReadAccess);
        if (result != 0) return result;
        result = EditViewAccess.CompareTo(other.EditViewAccess);
        if (result != 0) return result;
        result = PublishAccess.CompareTo(other.PublishAccess);
        if (result != 0) return result;
        result = CreatedBy.CompareTo(other.CreatedBy);
        if (result != 0) return result;
        result = ChangedBy.CompareTo(other.ChangedBy);
        if (result != 0) return result;

        // 4. Booleans
        result = IsPublished.CompareTo(other.IsPublished);
        if (result != 0) return result;
        result = IsDeleted.CompareTo(other.IsDeleted);
        if (result != 0) return result;

        // 5. DateTimes
        result = ChangedUtc.CompareTo(other.ChangedUtc);
        if (result != 0) return result;
        result = CreatedUtc.CompareTo(other.CreatedUtc);
        if (result != 0) return result;
        result = PublishedUtc.CompareTo(other.PublishedUtc);
        if (result != 0) return result;
        result = RetainedUtc.CompareTo(other.RetainedUtc);
        if (result != 0) return result;

        return ReleasedUtc.CompareTo(other.ReleasedUtc);
    }

    /// <summary>
    /// Required implementation of IComparable<T>.
    /// </summary>
    public int CompareTo(NodeCacheKey other) => CompareTo(in other);

    // --- 4. Operators (Recommended for Structs) ---

    public static bool operator ==(in NodeCacheKey left, in NodeCacheKey right) => left.Equals(in right);
    public static bool operator !=(in NodeCacheKey left, in NodeCacheKey right) => !left.Equals(in right);
}



