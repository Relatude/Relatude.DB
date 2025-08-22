using WAF.IO;
using System.Diagnostics.CodeAnalysis;
using WAF.Transactions;
using WAF.DataStores.Sets;
using System.Collections.Generic;

namespace WAF.DataStores.Relations;
public class OneOneIndex(SetRegister setRegister) : IRelationIndex {
    public bool IsSymmetric => true;
    readonly Dictionary<int, int> _rel = new();
    readonly RelationDataDictionary _relData = new();
    public IEnumerable<KeyValuePair<int, int>> AllSourceTarget => _rel;
    public bool Contains(int source, int target) => _relData.Contains(Math.Min(source, target), Math.Max(source, target));
    public bool ContainsSource(int source) => _rel.ContainsKey(source);
    public bool ContainsTarget(int target) => _rel.ContainsKey(target);
    public bool TryGetTarget(int source, [MaybeNullWhen(false)] out int target) => _rel.TryGetValue(source, out target);
    public bool TryGetSource(int target, [MaybeNullWhen(false)] out int source) => _rel.TryGetValue(target, out source);
    public int GetTo(int source) {
        if (TryGetTarget(source, out var r)) return r;
        throw new Exception("There is no relation target " + source.ToString());
    }
    public int GetFrom(int target) {
        if (TryGetSource(target, out var r)) return r;
        throw new Exception("There is no relation source " + target.ToString());
    }
    public int CountTarget(int source) => _rel.ContainsKey(source) ? 1 : 0;
    public int CountSource(int target) => _rel.ContainsKey(target) ? 1 : 0;
    public void Remove(int source, int target) {
        if (_rel.TryGetValue(source, out var oldTarget)) {
            _rel.Remove(oldTarget);
            if (source != oldTarget) _rel.Remove(source);
            _relData.Remove(Math.Min(source, target), Math.Max(source, target));
        } else {
            throw new Exception("Cannot remove unknown relation: " + source.ToString() + " -> " + target.ToString());
        }
    }
    public void DeleteIfReferenced(int id) {
        if (_rel.TryGetValue(id, out var source)) Remove(source, id);
        if (_rel.TryGetValue(id, out var target)) Remove(id, target);
    }
    public void Add(int source, int target, DateTime changedUtc) {
        if (Contains(source, target)) throw new ItemAlreadyInRelationException();
        if (_rel.TryGetValue(source, out var oldTarget)) {
            throw new Exception("Existing relation from " + source + " to " + oldTarget + ", must be removed first. ");
            //Remove(source, oldTarget);
        }
        if (_rel.TryGetValue(target, out var oldSource)) {
            throw new Exception("Existing relation from " + oldSource + " to " + target + ", must be removed first. ");
            //Remove(oldSource, target);
        }
        _rel.Add(source, target);
        if (source != target) _rel.Add(target, source);
        _relData.Add(Math.Min(source, target), Math.Max(source, target), changedUtc);
    }
    public DateTime GetDateTime(int source, int target) => _relData.Get(Math.Min(source, target), Math.Max(source, target));
    public void CompressMemory() {

    }
    public void SaveState(IAppendStream stream) {
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
            var target = (int)stream.ReadUInt();
            var changedUtc = stream.ReadDateTimeUtc();
            Add(source, target, changedUtc);
        }

    }
    public IdSet Get(int id, bool fromTargetToSource) {
        if (_rel.TryGetValue(id, out var related)) return setRegister.SingleValueIdSet(related);
        return IdSet.Empty;
    }
    public int CountRelated(int id, bool fromTargetToSource) {
        if (_rel.ContainsKey(id)) return 1;
        return 0;
    }
    public IEnumerable<RelData> Values => _relData.Values;

    public IEnumerable<RelData> GetOtherRelationsThatNeedsToRemovedBeforeAdd(int source, int target) {
        if (_rel.TryGetValue(source, out var oldTarget)) {
            var s = Math.Min(source, oldTarget);
            var t = Math.Max(source, oldTarget);
            yield return new RelData(source, oldTarget, _relData.Get(s, t));
        }
        if (_rel.TryGetValue(target, out var oldSource)) {
            var s = Math.Min(target, oldSource);
            var t = Math.Max(target, oldSource);
            yield return new RelData(oldSource, target, _relData.Get(s, t));
        }
    }
    public int TotalCount => _rel.Count;
}


