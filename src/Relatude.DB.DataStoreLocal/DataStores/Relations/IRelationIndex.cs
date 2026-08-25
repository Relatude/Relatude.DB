using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Relations;
public struct RelData{
    public RelData(int source, int target, DateTime dt) {
        Source = source;
        Target = target;
        DateTimeUtc = dt;
    }
    public int Source;
    public int Target;
    public DateTime DateTimeUtc;
}
public interface IRelationIndex {

    int TotalCount { get; }
    void Add(int source, int target, DateTime changedUtc);
    void Remove(int source, int target);
    bool Contains(int source, int target);
    void DeleteIfReferenced(int id);

    bool IsSymmetric { get; }

    IEnumerable<RelData> GetOtherRelationsThatNeedsToRemovedBeforeAdd(int source, int target);
    /// <summary>Moves <paramref name="moved"/> to <paramref name="toIndex"/> (clamped to the list bounds) within
    /// the ordered list of nodes related to <paramref name="owner"/> for the given direction, i.e. the list
    /// returned by <see cref="Get"/>(owner, fromTargetToSource). Throws if the pair is not related.
    /// No-op for single-valued sides (the "one" side of one-to-many, and all one-one/one-to-one lists).</summary>
    void Move(int owner, int moved, bool fromTargetToSource, int toIndex);
    /// <summary>The position of <paramref name="related"/> within the ordered list of nodes related to
    /// <paramref name="owner"/> for the given direction, or -1 if the pair is not related.
    /// Single valued sides return 0.</summary>
    int IndexOfRelated(int owner, int related, bool fromTargetToSource);
    IdSet Get(int id, bool fromTargetToSource);
    /// <summary>The distinct ids valid as first argument to <see cref="Get"/> for the given
    /// direction, i.e. the ids with at least one edge. Symmetric relations return all
    /// participants regardless of direction. O(1) to obtain, live view of the internal FileKeyUtility.</summary>
    IEnumerable<int> DistinctIds(bool fromTargetToSource);
    IEnumerable<RelData> Values { get; }
    DateTime GetDateTime(int source, int target);

    void CompressMemory();
    int CountRelated(int id, bool fromTargetToSource);
    int CountTarget(int source);
    int CountSource(int target);
    
    void SaveState(IAppendStream stream);
    void ReadState(BufferReader stream);

}
