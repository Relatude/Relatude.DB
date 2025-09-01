using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Relations;
public class ManyToManyIndex() : IRelationIndex {
    public bool IsSymmetric => false;
    readonly RelationDataDictionary _relData = new();
    readonly Dictionary<int, RelatedList> _relTargetBySource = new();
    readonly Dictionary<int, RelatedList> _relSourceByTarget = new();
    public RelatedList GetTarget(int target) {
        if (_relSourceByTarget.TryGetValue(target, out var r)) return r;
        throw new Exception("There is no relation source " + target.ToString());
    }
    public RelatedList GetSource(int source) {
        if (_relTargetBySource.TryGetValue(source, out var r)) return r;
        throw new Exception("There is no relation source " + source.ToString());
    }
    public bool Contains(int source, int target) => _relData.Contains(source, target);
    public int CountTarget(int source) => _relSourceByTarget.TryGetValue(source, out var r) ? r.Count : 0;
    public int CountSource(int target) => _relTargetBySource.TryGetValue(target, out var r) ? r.Count : 0;
    public void Remove(int source, int target) {
        if (_relTargetBySource.TryGetValue(source, out var targets)) {
            targets.Remove(target);
            if (targets.Count == 0) _relTargetBySource.Remove(source);
        } else {
            throw new ItemNotInRelationException();
        }
        if (_relSourceByTarget.TryGetValue(target, out var sources)) {
            sources.Remove(source);
            if (sources.Count == 0) _relSourceByTarget.Remove(target);
        } else {
            throw new ItemNotInRelationException();
        }
        _relData.Remove(source, target);
    }
    public void Add(int source, int target, DateTime changedUtc) {
        if (Contains(source, target)) throw new ItemAlreadyInRelationException();
        if (_relTargetBySource.TryGetValue(source, out var targets)) targets.Add(target);
        else _relTargetBySource.Add(source, new RelatedList(target));
        if (_relSourceByTarget.TryGetValue(target, out var sources)) sources.Add(source);
        else _relSourceByTarget.Add(target, new RelatedList(source));
        _relData.Add(source, target, changedUtc);
    }
    public DateTime GetDateTime(int source, int target) => _relData.Get(source, target);
    public void CompressMemory() { }
    public void SaveState(IAppendStream stream) {
        // this is saving duplicate data, and could be done more efficiently
        // but it is perserving original list order for every relation
        stream.WriteVerifiedInt(_relTargetBySource.Count);
        foreach (var (source, targets) in _relTargetBySource) {
            stream.WriteUInt((uint)source);
            stream.WriteVerifiedInt(targets.Count);
            foreach (var target in targets) stream.WriteUInt((uint)target);
        }
        stream.WriteVerifiedInt(_relSourceByTarget.Count);
        foreach (var (target, sources) in _relSourceByTarget) {
            stream.WriteUInt((uint)target);
            stream.WriteVerifiedInt(sources.Count);
            foreach (var source in sources) stream.WriteUInt((uint)source);
        }
        stream.WriteVerifiedInt(_relData.Count);
        foreach (var d in _relData.Values) {
            stream.WriteUInt((uint)d.Source);
            stream.WriteUInt((uint)d.Target);
            stream.WriteDateTimeUtc(d.DateTimeUtc);
        }
    }
    public void ReadState(IReadStream stream) {
        var count = stream.ReadVerifiedInt();
        for (int i = 0; i < count; i++) {
            var source = (int)stream.ReadUInt();
            var targets = new RelatedList();
            var targetCount = stream.ReadVerifiedInt();
            for (int j = 0; j < targetCount; j++) targets.Add((int)stream.ReadUInt());
            _relTargetBySource.Add(source, targets);
        }
        count = stream.ReadVerifiedInt();
        for (int i = 0; i < count; i++) {
            var target = (int)stream.ReadUInt();
            var sources = new RelatedList();
            var sourceCount = stream.ReadVerifiedInt();
            for (int j = 0; j < sourceCount; j++) sources.Add((int)stream.ReadUInt());
            _relSourceByTarget.Add(target, sources);
        }
        count = stream.ReadVerifiedInt();
        for (int i = 0; i < count; i++) {
            var source = (int)stream.ReadUInt();
            var target = (int)stream.ReadUInt();
            var dateTimeUtc = stream.ReadDateTimeUtc();
            _relData.Add(source, target, dateTimeUtc);
        }
    }
    public void DeleteIfReferenced(int id) {
        if (_relSourceByTarget.TryGetValue(id, out var sources)) foreach (var source in sources) Remove(source, id);
        if (_relTargetBySource.TryGetValue(id, out var targets)) foreach (var target in targets) Remove(id, target);
    }
    public IdSet Get(int id, bool fromTargetToSource) {
        if (fromTargetToSource) {
            if (_relSourceByTarget.TryGetValue(id, out var sources)) return sources.ToIdSet();
        } else {
            if (_relTargetBySource.TryGetValue(id, out var targets)) return targets.ToIdSet();
        }
        return IdSet.Empty;
    }
    public int CountRelated(int id, bool fromTargetToSource) {
        if (fromTargetToSource) {
            if (_relSourceByTarget.TryGetValue(id, out var sources)) return sources.Count;
        } else {
            if (_relTargetBySource.TryGetValue(id, out var targets)) return targets.Count;
        }
        return 0;
    }
    public IEnumerable<RelData> Values => _relData.Values;
    public IEnumerable<RelData> GetOtherRelationsThatNeedsToRemovedBeforeAdd(int source, int target) {
        yield break;
    }
    public int TotalCount => _relData.Count;
}


