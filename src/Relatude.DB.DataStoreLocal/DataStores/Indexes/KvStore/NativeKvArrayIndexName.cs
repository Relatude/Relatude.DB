namespace Relatude.DB.DataStores.Indexes.KvStore;

/// <summary>
/// The KV index name behind a persisted array index (int, string or Guid), each of which packs a
/// node's whole array into one binary value.
/// <para>
/// These started out on the engine's sorted layout, where a value is half of a B+Tree composite key:
/// capped at about a kilobyte and escaped to stay prefix-free. An array index needs none of that —
/// it reads the whole mapping back once (<c>ReadAllPersisted</c>), sets one id and removes one id,
/// never ordering or looking up by value. The hash layout is exactly that contract: it keeps values
/// as opaque payloads, spills the large ones onto overflow pages and skips the escaping, so a node
/// can hold an array of any size. Its enumeration comes out in bucket order rather than by id, which
/// costs nothing here — the in-memory mirror these entries feed is order-independent.
/// </para>
/// <para>
/// A name is bound to one layout in the engine catalog, so the move needs a new name: opening the
/// old one as a hash index throws instead. Hence the suffix below, which is also the whole migration.
/// A store still carrying the sorted layout finds nothing under the new name, creates an empty index
/// reporting timestamp 0, and the startup loader replays the WAL into it — the same path a newly
/// added indexed property already takes. The old sorted index is then open to nobody, and the
/// <c>DeleteUnopenedIndexes</c> pass that runs right after every open drops it and frees its pages.
/// </para>
/// </summary>
internal static class NativeKvArrayIndexName {

    /// <summary>
    /// Suffix marking the hash-layout generation of an array index. An index unique key is a
    /// property guid joined to an optional culture and sub key by '_' (see
    /// <see cref="IndexFactory.getUniqueKey"/>), so a '#' can never appear in one and this name
    /// cannot collide with another index's. The legacy (sorted) name is the unique key itself.
    /// </summary>
    public const string HashLayoutSuffix = "#arrays2";

    /// <summary>The KV index holding <paramref name="uniqueKey"/>'s packed arrays.</summary>
    public static string For(string uniqueKey) => uniqueKey + HashLayoutSuffix;
}
