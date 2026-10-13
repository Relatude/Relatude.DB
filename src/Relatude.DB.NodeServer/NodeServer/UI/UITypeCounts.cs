using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The node count of each type, as the pages that list types show them: the Query page's type picker
/// and the data model editor.
///
/// Counting is a query, and a query waits for the store lock, which a transaction holds for as long as it
/// runs - so a page that counted on load showed nothing at all until a long import had finished. Here a
/// count waits a moment for the lock and then gives up; the types it did not get to are left out, the
/// answer says it is incomplete, and the page asks again a little later.
/// </summary>
static class UITypeCounts {
    /// <summary>How long a count waits for the store lock before the page is told the store is busy.</summary>
    static readonly TimeSpan lockWait = TimeSpan.FromMilliseconds(100);
    /// <summary>
    /// The counts of <paramref name="typeIds"/>. A type the engine cannot count is left out, as is every
    /// type after the store turned out to be busy - the rest would wait for the same transaction - in
    /// which case <paramref name="complete"/> is false.
    /// </summary>
    public static Dictionary<Guid, long> Count(NodeStore store, IEnumerable<Guid> typeIds, QueryContext ctx, out bool complete) {
        var counts = new Dictionary<Guid, long>();
        var countCtx = ctx.WithLockWait(lockWait);
        complete = true;
        foreach (var id in typeIds) {
            try {
                counts[id] = store.QueryType(id, countCtx).Count(); // the id set per type is maintained: a lookup, not a scan
            } catch (StoreBusyException) {
                complete = false;
                break;
            } catch {
                // one type the engine cannot count must not take the whole list down
            }
        }
        return counts;
    }
}
