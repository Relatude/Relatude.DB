using Relatude.DB.IO;
using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Transactions;
using Relatude.DB.DataStores.Sets;
using System.Collections.Generic;

namespace Relatude.DB.DataStores.Relations;
public class OneToOneIndex(SetRegister setRegister) : IRelationIndex {
    public bool IsSymmetric => false;
    readonly Dictionary<int, int> _targetBySource = new();
    readonly Dictionary<int, int> _sourceByTarget = new();
    readonly RelationDataDictionary _relData = new();
    public IEnumerable<KeyValuePair<int, int>> AllFromTo => _targetBySource;
    public bool Contains(int source, int target) => _targetBySource.TryGetValue(source, out var r) ? r.Equals(target) : false;
    public bool ContainsSource(int source) => _targetBySource.ContainsKey(source);
    public bool ContainsTarget(int target) => _sourceByTarget.ContainsKey(target);
    public bool TryGetTarget(int source, [MaybeNullWhen(false)] out int target) => _targetBySource.TryGetValue(source, out target);
    public bool TryGetSource(int target, [MaybeNullWhen(false)] out int source) => _sourceByTarget.TryGetValue(target, out source);
    public int GetTarget(int source) {
        if (TryGetTarget(source, out var r)) return r;
        throw new Exception("There is no relation target " + source.ToString());
    }
    public int GetSource(int target) {
        if (TryGetSource(target, out var r)) return r;
        throw new Exception("There is no relation source " + target.ToString());
    }
    public int CountTarget(int source) => _targetBySource.ContainsKey(source) ? 1 : 0;
    public int CountSource(int target) => _sourceByTarget.ContainsKey(target) ? 1 : 0;
    public void Remove(int source, int target) {
        if (_targetBySource.TryGetValue(source, out var oldTo)) {
            _sourceByTarget.Remove(oldTo);
            _targetBySource.Remove(source);
            _relData.Remove(source, target);
        } else {
            throw new Exception("Cannot remove unknown relation: " + source.ToString() + " -> " + target.ToString());
        }
    }
    public void DeleteIfReferenced(int id) {
        if (_sourceByTarget.TryGetValue(id, out var source)) Remove(source, id);
        if (_targetBySource.TryGetValue(id, out var target)) Remove(id, target);
    }
    public void Add(int source, int target, DateTime changedUtc) {
        if (Contains(source, target)) throw new ItemAlreadyInRelationException();
        if (_targetBySource.TryGetValue(source, out var oldTarget)) {
            throw new Exception("Existing relation from " + source + " to " + oldTarget + ", must be removed first. ");
            // Remove(source, oldTarget);
        }
        if (_sourceByTarget.TryGetValue(target, out var oldSource)) {
            throw new Exception("Existing relation from " + oldSource + " to " + target + ", must be removed first. ");
            // Remove(oldSource, target);
        }
        _targetBySource.Add(source, target);
        _sourceByTarget.Add(target, source);
        _relData.Add(source, target, changedUtc);
    }
    public DateTime GetDateTime(int source, int target) => _relData.Get(source, target);
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
            int source = (int)stream.ReadUInt();
            int target = (int)stream.ReadUInt();
            var dateTimeUtc = stream.ReadDateTimeUtc();
            Add(source, target, dateTimeUtc);
        }
    }
    public IdSet Get(int id, bool fromTargetToSource) {
        if (fromTargetToSource) {
            if (_sourceByTarget.TryGetValue(id, out var source)) return setRegister.SingleValueIdSet(source);
        } else {
            if (_targetBySource.TryGetValue(id, out var target)) return setRegister.SingleValueIdSet(target);
        }
        return IdSet.Empty;
    }
    public int CountRelated(int id, bool fromTargetToSource) {
        if (fromTargetToSource) {
            if (_sourceByTarget.ContainsKey(id)) return 1;
        } else {
            if (_targetBySource.ContainsKey(id)) return 1;
        }
        return 0;
    }
    public IEnumerable<RelData> Values => _relData.Values;
    public IEnumerable<RelData> GetOtherRelationsThatNeedsToRemovedBeforeAdd(int source, int target) {
        if (_targetBySource.TryGetValue(source, out var oldTarget)) {
            yield return new RelData(source, oldTarget, _relData.Get(source, oldTarget));
        }
        if (_sourceByTarget.TryGetValue(target, out var oldSource)) {
            yield return new RelData(oldSource, target, _relData.Get(oldSource, target));
        }
        yield break;
    }
    public int TotalCount => _targetBySource.Count;
}


