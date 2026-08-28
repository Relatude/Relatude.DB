using Relatude.DB.Datamodels;

namespace Relatude.DB.NodeServer.Models;

/// <summary>
/// The reading context of a query, as it arrives over HTTP. Every member is optional: what is left out
/// keeps the default of a plain <see cref="QueryContext"/>, and leaving the whole model out reads with
/// the context of the store itself.
/// <para>
/// The context decides what the query is allowed to see (read access of the user and the groups the
/// user belongs to), which culture it reads in, and whether deleted, unpublished or hidden nodes are
/// included. Note that a caller who can reach this endpoint can name any user, including the master
/// admin: it is part of the authenticated admin API and is not a place to enforce end user permissions.
/// </para>
/// </summary>
public class QueryContextModel {
    /// <summary>Read as this user. Empty or omitted reads as an anonymous user.</summary>
    public Guid? UserId { get; set; }
    /// <summary>Culture to read in, by code. Cannot be combined with <see cref="CultureId"/>.</summary>
    public string? CultureCode { get; set; }
    /// <summary>Culture to read in, by node id. Cannot be combined with <see cref="CultureCode"/>.</summary>
    public Guid? CultureId { get; set; }
    /// <summary>Include nodes flagged as deleted.</summary>
    public bool? IncludeDeleted { get; set; }
    /// <summary>Ignore the release and expire dates, so unpublished nodes are returned. Used by previews.</summary>
    public bool? IncludeUnpublished { get; set; }
    /// <summary>Fall back to another culture when a node has no content in the one asked for.</summary>
    public bool? IncludeCultureFallback { get; set; }
    /// <summary>Return only nodes that do have a culture, skipping culture invariant ones.</summary>
    public bool? OnlyWithCulture { get; set; }
    /// <summary>Also apply the edit view access of each node, as an editing UI does.</summary>
    public bool? EditView { get; set; }
    /// <summary>Include node types marked as hidden.</summary>
    public bool? IncludeHidden { get; set; }
    /// <summary>Return only nodes of the exact type queried, leaving out the types inheriting from it.</summary>
    public bool? ExcludeDescendants { get; set; }
    /// <summary>Limit the result to these collections. Omitted means no collection filtering.</summary>
    public Guid[]? CollectionIds { get; set; }
    /// <summary>Evaluate release and expire dates against this moment instead of now. Used to preview a future state.</summary>
    public DateTime? NowUtc { get; set; }

    /// <summary>
    /// Builds the context to read with, or null when no context was given, which leaves the store
    /// default in place.
    /// </summary>
    public static QueryContext? Convert(QueryContextModel? model) {
        if (model == null) return null;
        if (model.CultureCode != null && model.CultureId.HasValue)
            throw new InvalidOperationException("A query context can specify either " + nameof(CultureCode) + " or " + nameof(CultureId) + ", not both. ");
        var ctx = QueryContext.Default;
        if (model.UserId.HasValue) ctx = ctx.User(model.UserId.Value);
        if (model.CultureCode != null) ctx = ctx.Culture(model.CultureCode);
        if (model.CultureId.HasValue) ctx = ctx.Culture(model.CultureId.Value);
        if (model.IncludeDeleted.HasValue) ctx = ctx.Deleted(model.IncludeDeleted.Value);
        if (model.IncludeUnpublished.HasValue) ctx = ctx.Unpublished(model.IncludeUnpublished.Value);
        if (model.IncludeCultureFallback.HasValue) ctx = ctx.CultureFallbacks(model.IncludeCultureFallback.Value);
        if (model.OnlyWithCulture.HasValue) ctx = ctx.RequireCulture(model.OnlyWithCulture.Value);
        if (model.EditView.HasValue) ctx = ctx.EditViewMode(model.EditView.Value);
        if (model.IncludeHidden.HasValue) ctx = ctx.Hidden(model.IncludeHidden.Value);
        if (model.ExcludeDescendants.HasValue) ctx = ctx.Descendants(model.ExcludeDescendants.Value);
        if (model.CollectionIds != null) ctx = ctx.Collections(model.CollectionIds);
        if (model.NowUtc.HasValue) ctx = ctx.Now(model.NowUtc.Value);
        return ctx;
    }
}
