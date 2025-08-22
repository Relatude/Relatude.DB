using WAF.IO;
using System.Diagnostics.CodeAnalysis;
using WAF.DataStores.Sets;
namespace WAF.DataStores.Relations;
public class OneToManyIndex(SetRegister setRegister) : IRelationIndex {
    public bool IsSymmetric => false;
    // source (One ) target ( Many ) 
    readonly Dictionary<int, RelatedList> _targetBySource = new();
    readonly Dictionary<int, int> _sourceByTarget = new();
    readonly RelationDataDictionary _relData = new();
    public bool TryGetSource(int target, [MaybeNullWhen(false)] out int source) => _sourceByTarget.TryGetValue(target, out source);
    public bool TryGetTarget(int source, [MaybeNullWhen(false)] out IdSet target) {
        if (_targetBySource.TryGetValue(source, out var targetList)) {
            target = targetList.ToIdSet();
            return true;
        } else {
            target = null;
            return false;
        }
    }
    public IdSet GetTargets(int source) => TryGetTarget(source, out var r) ? r : throw new Exception("No relation target " + source);
    public int GetSource(int target) => TryGetSource(target, out var r) ? r : throw new Exception("No relation source " + target);
    public int CountSource(int target) => _sourceByTarget.ContainsKey(target) ? 1 : 0;
    public int CountTarget(int source) => _targetBySource.TryGetValue(source, out var r) ? r.Count : 0;
    public void Remove(int source, int target) {
        if (_sourceByTarget.ContainsKey(target)) {
            _sourceByTarget.Remove(target);
        } else {
            throw new Exception("No relation target " + target);
        }
        if (_targetBySource.TryGetValue(source, out var targets)) {
            targets.Remove(target);
            if (targets.Count == 0) _targetBySource.Remove(source);
        } else {
            throw new Exception("No relation source " + source);
        }
        _relData.Remove(source, target);
    }
    public bool Contains(int source, int target) {
        if (_targetBySource.TryGetValue(source, out var targets)) {
            return targets.Contains(target);
        } else {
            return false;
        }
    }
    public void Add(int source, int target, DateTime changedUtc) {
        if (Contains(source, target)) throw new ItemAlreadyInRelationException();
        if (_sourceByTarget.TryGetValue(target, out var oldSource)) {
            throw new Exception("Existing relation from " + oldSource + " to " + target + ", must be removed first. ");
            //Remove(oldSource, target); // To preserver One target Many rule
        } else {
            _sourceByTarget.Add(target, source);
        }
        if (_targetBySource.TryGetValue(source, out var targets)) {
            targets.Add(target);
        } else {
            _targetBySource.Add(source, new(target));
        }
        _relData.Add(source, target, changedUtc);
    }
    public DateTime GetDateTime(int source, int target) => _relData.Get(source, target);
    public void CompressMemory() {

    }
    public void SaveState(IAppendStream stream) {
        stream.WriteVerifiedInt(_sourceByTarget.Count);
        foreach (var (target, source) in _sourceByTarget) {
            stream.WriteUInt((uint)target);
            stream.WriteUInt((uint)source);
        }
        stream.WriteVerifiedInt(_targetBySource.Count);
        foreach (var (source, targets) in _targetBySource) {
            stream.WriteUInt((uint)source);
            stream.WriteVerifiedInt(targets.Count);
            foreach (var target in targets) stream.WriteUInt((uint)target);
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
            var target = (int)stream.ReadUInt();
            var source = (int)stream.ReadUInt();
            _sourceByTarget.Add(target, source);
        }
        count = stream.ReadVerifiedInt();
        for (int i = 0; i < count; i++) {
            var source = (int)stream.ReadUInt();
            var targets = new RelatedList();
            var targetCount = stream.ReadVerifiedInt();
            for (int j = 0; j < targetCount; j++) targets.Add((int)stream.ReadUInt());
            _targetBySource.Add(source, targets);
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
        if (_sourceByTarget.TryGetValue(id, out var source)) Remove(source, id);
        if (_targetBySource.TryGetValue(id, out var targets)) foreach (var target in targets) Remove(id, target);
    }
    public IdSet Get(int id, bool fromTargetToSource) {
        if (fromTargetToSource) {
            if (_sourceByTarget.TryGetValue(id, out var source)) return setRegister.SingleValueIdSet(source);
        } else {
            if (_targetBySource.TryGetValue(id, out var targets)) return targets.ToIdSet();
        }
        return IdSet.Empty;
    }
    public int CountRelated(int id, bool fromTargetToSource) {
        if (fromTargetToSource) {
            if(_sourceByTarget.ContainsKey(id)) return 1;
        } else {
            if (_targetBySource.TryGetValue(id, out var targets)) return targets.Count;
        }
        return 0;
    }
    public IEnumerable<RelData> Values => _relData.Values;
    public IEnumerable<RelData> GetOtherRelationsThatNeedsToRemovedBeforeAdd(int source, int target) {
        if (_sourceByTarget.TryGetValue(target, out var oldSource)) {
            yield return new(oldSource, target, _relData.Get(oldSource, target));
        }
        yield break;
    }
    public int TotalCount => _sourceByTarget.Count;
}
