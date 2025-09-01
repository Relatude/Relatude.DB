using Relatude.DB.Common;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Relations;
internal delegate void AddRelation(int source, int target, DateTime dateTimeUtc);
internal class RelationDataDictionary {
    readonly Dictionary<RKey, DateTime> _relData = new(new RelationKeyComparer());
    public void Add(int source, int target, DateTime dateTimeUtc) {
        _relData.Add(new(source, target), dateTimeUtc);
    }
    public bool Contains(int source, int target) => _relData.ContainsKey(new(source, target));
    public void Remove(int source, int target) {
        _relData.Remove(new(source, target));
    }
    public DateTime Get(int source, int target) => _relData[new(source, target)];
    public IEnumerable<RelData> Values => _relData.Select(x => new RelData(x.Key.Source, x.Key.Target, x.Value));
    public int Count => _relData.Count;
    internal class RelationKeyComparer : IEqualityComparer<RKey> {
        public bool Equals(RKey x, RKey y) => x.Source.Equals(y.Source) && x.Target.Equals(y.Target);
        public int GetHashCode(RKey obj) => obj.Source.GetHashCode() + obj.Target.GetHashCode();
    }
    internal struct RKey {
        public RKey(int source, int target) {
            Source = source;
            Target = target;
        }
        public int Source;
        public int Target;
    }
}
