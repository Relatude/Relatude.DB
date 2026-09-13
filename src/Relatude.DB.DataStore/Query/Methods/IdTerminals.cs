using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Methods;

/// <summary>
/// The shared half of the terminals that answer from the ids of a result and what its indexes know,
/// never from a node: SelectId(), Buckets(), Coordinates() and Words(). Evaluated inside the query,
/// under the store's read lock, and handing back plain data, so a result of a million nodes costs
/// its ids and not its nodes - which is what a picture of a whole result needs, and all a remote
/// client would have to carry.
/// </summary>
internal static class TerminalSource {
    /// <summary>
    /// The node collection a terminal evaluates against. A facet clause contributes its selection
    /// and nothing else: counting its buckets would be wasted work, and with no facets named it
    /// would count every automatic facet. A Page on such a clause pages the selection. Anything
    /// else is evaluated as it stands; a facet result that was evaluated after all yields its page.
    /// </summary>
    public static IStoreNodeDataCollection Nodes(IExpression input, IVariables vars, string method) {
        if (input is FacetMethod facets) return facets.EvaluateSelection(vars);
        if (input is PageMethod page && page.Input is FacetMethod pagedFacets)
            return (IStoreNodeDataCollection)pagedFacets.EvaluateSelection(vars).Page(page.PageIndex, page.PageSize);
        var result = input.Evaluate(vars);
        if (result is FacetQueryResultData fq) return fq.Result;
        if (result is IStoreNodeDataCollection nodes) return nodes;
        throw new Exception(method + "() can only be used on a collection of nodes. ");
    }
}

/// <summary>One property a result is bucketed by, and how: by its values, by ranges of them, or as the property prefers (null).</summary>
public sealed record BucketBy(string Property, Guid PropertyId, bool? IsRange);

/// <summary>A clause that buckets its result by properties: Buckets() itself, and Coordinates() for the colours of its points.</summary>
internal interface IBucketBuilder {
    void AddBucket(string property, bool? isRange);
    void SetBucketOptions(int maxBuckets, bool includeMissing);
}

/// <summary>The bucketing shared by <see cref="BucketsMethod"/> and <see cref="CoordinatesMethod"/>.</summary>
internal static class BucketGroups {
    /// <summary>How many distinct values one property may be bucketed into unless told otherwise: past this the smaller buckets go unassigned.</summary>
    public const int DefaultMaxBuckets = 500;

    public static Guid PropertyId(Datamodel dm, string idString) {
        var id = dm.GetPropertyGuid(idString);
        if (!dm.Properties.ContainsKey(id)) throw new Exception("Unknown property \"" + idString + "\". ");
        return id;
    }

    /// <summary>
    /// The buckets of the collection by each property asked for, with the ids of every bucket
    /// materialized: the answer is plain data, read while the lock is held, and none of the
    /// collection's own sets is enumerated after it.
    /// </summary>
    public static PropertyBuckets[] Of(IStoreNodeDataCollection nodes, IReadOnlyList<BucketBy> by, int maxBuckets, bool includeMissing, QueryContext ctx, string method) {
        if (by.Count == 0) return [];
        if (nodes is not IBucketSource source) throw new Exception(method + "() cannot bucket this collection without reading its nodes. ");
        var result = new PropertyBuckets[by.Count];
        for (var i = 0; i < by.Count; i++) {
            var buckets = source.Bucket(by[i].PropertyId, by[i].IsRange, includeMissing, maxBuckets, ctx);
            var copied = new ValueBucket[buckets.Count];
            for (var b = 0; b < buckets.Count; b++) copied[b] = new ValueBucket(buckets[b].Value, buckets[b].Ids.ToArray(), buckets[b].Count);
            result[i] = new PropertyBuckets(by[i].PropertyId, copied);
        }
        return result;
    }

    /// <summary>The clauses as written: one Add*Bucket per property, and the options when they differ from the defaults.</summary>
    public static string Clauses(IReadOnlyList<BucketBy> by, int maxBuckets, bool includeMissing) {
        var sb = new System.Text.StringBuilder();
        foreach (var b in by) {
            sb.Append(b.IsRange switch { null => ".AddBucket(", false => ".AddValueBucket(", true => ".AddRangeBucket(" });
            sb.Append(b.Property.ToStringLiteral()).Append(')');
        }
        if (maxBuckets != DefaultMaxBuckets || !includeMissing) {
            sb.Append(".SetBucketOptions(").Append(maxBuckets).Append(", ").Append(includeMissing ? "true" : "false").Append(')');
        }
        return sb.ToString();
    }
}

/// <summary>Where each id of an array sits in it, and orders expressed as positions into it.</summary>
internal static class IdPositions {
    /// <summary>
    /// A lookup from id to position: a flat array when the ids are dense enough for one, a
    /// dictionary otherwise (a result of a few thousand out of millions would waste the array).
    /// -1 for an id that is not there.
    /// </summary>
    public static Func<int, int> Of(int[] ids) {
        if (ids.Length == 0) return _ => -1;
        var max = 0;
        foreach (var id in ids) if (id > max) max = id;
        if (max <= ids.Length * 8L + 4096) {
            var byId = new int[max + 1];
            Array.Fill(byId, -1);
            for (var i = 0; i < ids.Length; i++) if (ids[i] >= 0) byId[ids[i]] = i;
            return id => id >= 0 && id < byId.Length ? byId[id] : -1;
        }
        var map = new Dictionary<int, int>(ids.Length);
        for (var i = 0; i < ids.Length; i++) map[ids[i]] = i;
        return id => map.TryGetValue(id, out var at) ? at : -1;
    }
    /// <summary>
    /// The positions into <paramref name="ids"/> in the order <paramref name="ordered"/> names them;
    /// an id the order leaves out comes last, in the array's own order, so the result is always a
    /// permutation of the positions.
    /// </summary>
    public static int[] OrderOf(int[] ids, IEnumerable<int> ordered) {
        var position = Of(ids);
        var order = new int[ids.Length];
        var placed = new bool[ids.Length];
        var k = 0;
        void place(int at) {
            if (at < 0 || placed[at]) return;
            placed[at] = true;
            order[k++] = at;
        }
        foreach (var id in ordered) place(position(id));
        for (var at = 0; at < ids.Length; at++) place(at);
        return order;
    }
}
