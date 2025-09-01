using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Relations;
public class ManyManyIndex() : IRelationIndex {
    public bool IsSymmetric => true;
    readonly RelationDataDictionary _relData = new();
    readonly Dictionary<int, RelatedList> _rel = new();
    public RelatedList GetTarget(int target) {
        if (_rel.TryGetValue(target, out var r)) return r;
        throw new Exception("There is no relation source " + target.ToString());
    }
    public RelatedList GetSource(int source) {
        if (_rel.TryGetValue(source, out var r)) return r;
        throw new Exception("There is no relation source " + source.ToString());
    }
    public bool Contains(int source, int target) => _relData.Contains(Math.Min(source, target), Math.Max(source, target));
    public int CountTarget(int source) => _rel.TryGetValue(source, out var r) ? r.Count : 0;
    public int CountSource(int target) => _rel.TryGetValue(target, out var r) ? r.Count : 0;
    public void Remove(int source, int target) {
        if (_rel.TryGetValue(source, out var tos)) {
            tos.Remove(target);
            if (tos.Count == 0) _rel.Remove(source);
        } else {
            throw new ItemNotInRelationException();
        }
        if (source != target) {
            if (_rel.TryGetValue(target, out var froms)) {
                froms.Remove(source);
                if (froms.Count == 0) _rel.Remove(target);
            } else {
                throw new ItemNotInRelationException();
            }
        }
        _relData.Remove(Math.Min(source, target), Math.Max(source, target));
    }
    public void Add(int source, int target, DateTime changedUtc) {
        if (Contains(source, target)) throw new ItemAlreadyInRelationException();
        if (_rel.TryGetValue(source, out var tos)) tos.Add(target);
        else _rel.Add(source, new RelatedList(target));
        if (source != target) {
            if (_rel.TryGetValue(target, out var froms)) froms.Add(source);
            else _rel.Add(target, new RelatedList(source ));
        }
        _relData.Add(Math.Min(source, target), Math.Max(source, target), changedUtc);

    }
    public DateTime GetDateTime(int source, int target) => _relData.Get(Math.Min(source, target), Math.Max(source, target));
    public void CompressMemory() {

    }
    public void SaveState(IAppendStream stream) {
        stream.WriteVerifiedInt(_rel.Count);
        foreach (var (source, targets) in _rel) {
            stream.WriteUInt((uint)source);
            stream.WriteVerifiedInt(targets.Count);
            foreach (var target in targets) stream.WriteUInt((uint)target);
        }
        stream.WriteVerifiedInt(_relData.Count);
        foreach(var d in _relData.Values) {
            stream.WriteUInt((uint)d.Source);
            stream.WriteUInt((uint)d.Target);
            stream.WriteDateTimeUtc(d.DateTimeUtc);
        }
    }
    public void ReadState(IReadStream stream) {
        var count = stream.ReadVerifiedInt();
        for (int i = 0; i < count; i++) {
            var source = (int)stream.ReadUInt();
            var targetCount = stream.ReadVerifiedInt();
            var targets = new RelatedList();
            for (int j = 0; j < targetCount; j++) targets.Add((int)stream.ReadUInt());
            _rel.Add(source, targets);
        }
        count = stream.ReadVerifiedInt();
        for (int i = 0; i < count; i++) {
            var source = (int)stream.ReadUInt();
            var target = (int)stream.ReadUInt();
            var changedUtc = stream.ReadDateTimeUtc();
            _relData.Add(source, target, changedUtc);
        }

    }
    public void DeleteIfReferenced(int id) {
        if (_rel.TryGetValue(id, out var sources)) foreach (var source in sources) Remove(source, id);
        if (_rel.TryGetValue(id, out var targets)) foreach (var target in targets) Remove(id, target);
    }
    public IdSet Get(int id, bool fromTargetToSource) {
        if (_rel.TryGetValue(id, out var related)) return related.ToIdSet();
        return IdSet.Empty;
    }
    public int CountRelated(int id, bool fromTargetToSource) {
        if (_rel.TryGetValue(id, out var related)) return related.Count;
        return 0;
    }
    public IEnumerable<RelData> Values => _relData.Values;
    public IEnumerable<RelData> GetOtherRelationsThatNeedsToRemovedBeforeAdd(int source, int target) {
        yield break;
    }

    public IEnumerable<int> GetAllIds_forDebugging() {
        HashSet<int> ids = new();
        foreach (var r in _rel) {
            ids.Add(r.Key);
            foreach (var t in r.Value) ids.Add(t);
        }
        return ids;
    }
    public int TotalCount => _rel.Count;
}
