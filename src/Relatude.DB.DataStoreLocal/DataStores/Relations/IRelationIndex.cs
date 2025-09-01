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
    IdSet Get(int id, bool fromTargetToSource);
    IEnumerable<RelData> Values { get; }
    DateTime GetDateTime(int source, int target);

    void CompressMemory();
    int CountRelated(int id, bool fromTargetToSource);
    int CountTarget(int source);
    int CountSource(int target);
    
    void SaveState(IAppendStream stream);
    void ReadState(IReadStream stream);

}
