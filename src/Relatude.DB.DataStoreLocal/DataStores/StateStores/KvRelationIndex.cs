using System.Runtime.InteropServices;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datastores.Indexes.BTreeIndex;
using Relatude.DB.DataStores.Relations;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.StateStores;

/// <summary>
/// One relation in the native state store: an ordered id list per owner and direction, and the edge
/// timestamps keyed by the (source, target) pair. Serves every relation kind; the kind decides the
/// symmetry and which existing edges an add displaces. The constraints themselves are checked by
/// <see cref="Definitions.Relation"/> before an add reaches the index, as for the memory indexes.
/// </summary>
sealed class KvRelationIndex : IRelationIndex {
    readonly RelationType _type;
    readonly IIntIndex<byte[]> _targetsBySource;
    readonly IIntIndex<byte[]> _sourcesByTarget; // the same index as _targetsBySource when symmetric
    readonly IUlongIndex<long> _edges;
    // the id set handed out for an owner keeps its state id until the owner's list changes, so the
    // set caches keyed on state ids keep working; bounded, an evicted owner just gets a fresh id
    readonly Cache<long, long> _stateIds = new(100_000);
    public KvRelationIndex(IStorageEngine storage, Guid relationId, RelationType type) {
        _type = type;
        var name = "rel_" + relationId.ToString("N");
        _targetsBySource = storage.OpenOrCreateIntHashIndex<byte[]>(name + "_t");
        _sourcesByTarget = IsSymmetric ? _targetsBySource : storage.OpenOrCreateIntHashIndex<byte[]>(name + "_s");
        _edges = storage.OpenOrCreateUlongHashIndex<long>(name + "_e");
    }
    public bool IsSymmetric => _type is RelationType.OneOne or RelationType.ManyMany;
    public int TotalCount => _edges.Count;
    static ulong pack(int source, int target) => ((ulong)(uint)source << 32) | (uint)target;
    ulong edgeKey(int source, int target) => IsSymmetric ? pack(Math.Min(source, target), Math.Max(source, target)) : pack(source, target);
    static int[] list(IIntIndex<byte[]> index, int owner) => index.TryGetValue(owner, out var bytes) ? MemoryMarshal.Cast<byte, int>(bytes).ToArray() : [];
    static void save(IIntIndex<byte[]> index, int owner, int[] ids) {
        if (ids.Length == 0) index.Remove(owner);
        else index.Set(owner, MemoryMarshal.AsBytes<int>(ids).ToArray());
    }
    IIntIndex<byte[]> lists(bool fromTargetToSource) => fromTargetToSource ? _sourcesByTarget : _targetsBySource;
    long stateKey(int owner, bool fromTargetToSource) => ((long)owner << 1) | (fromTargetToSource && !IsSymmetric ? 1L : 0L);
    void invalidate(int owner, bool fromTargetToSource) => _stateIds.Clear_EvenIf0Size(stateKey(owner, fromTargetToSource));

    public bool Contains(int source, int target) => _edges.ContainsKey(edgeKey(source, target));
    public void Add(int source, int target, DateTime changedUtc) {
        if (Contains(source, target)) throw new ItemAlreadyInRelationException();
        var occupied = _type switch {
            RelationType.OneOne => _targetsBySource.ContainsKey(source) || _targetsBySource.ContainsKey(target),
            RelationType.OneToOne => _targetsBySource.ContainsKey(source) || _sourcesByTarget.ContainsKey(target),
            RelationType.OneToMany => _sourcesByTarget.ContainsKey(target),
            _ => false,
        };
        if (occupied) throw new ExceptionWithoutIntegrityLoss("Existing relation of " + source + " or " + target + " must be removed first. ");
        append(_targetsBySource, source, target, false);
        if (!IsSymmetric || source != target) append(_sourcesByTarget, target, source, true);
        _edges.Set(edgeKey(source, target), changedUtc.Ticks);
    }
    void append(IIntIndex<byte[]> index, int owner, int related, bool fromTargetToSource) {
        save(index, owner, [.. list(index, owner), related]);
        invalidate(owner, fromTargetToSource);
    }
    public void Remove(int source, int target) {
        if (!Contains(source, target)) throw new ItemNotInRelationException();
        _edges.Remove(edgeKey(source, target));
        removeFrom(_targetsBySource, source, target, false);
        if (!IsSymmetric || source != target) removeFrom(_sourcesByTarget, target, source, true);
    }
    void removeFrom(IIntIndex<byte[]> index, int owner, int related, bool fromTargetToSource) {
        var ids = list(index, owner);
        var i = Array.IndexOf(ids, related);
        if (i < 0) throw new ItemNotInRelationException();
        save(index, owner, [.. ids[..i], .. ids[(i + 1)..]]);
        invalidate(owner, fromTargetToSource);
    }
    public DateTime GetDateTime(int source, int target) => new(_edges.GetValue(edgeKey(source, target)), DateTimeKind.Utc);
    public void Move(int owner, int moved, bool fromTargetToSource, int toIndex) {
        var index = lists(fromTargetToSource);
        var ids = list(index, owner);
        var from = Array.IndexOf(ids, moved);
        if (from < 0) throw new ItemNotInRelationException();
        toIndex = Math.Clamp(toIndex, 0, ids.Length - 1);
        if (from == toIndex) return;
        var reordered = ids.ToList();
        reordered.RemoveAt(from);
        reordered.Insert(toIndex, moved);
        save(index, owner, [.. reordered]);
        invalidate(owner, fromTargetToSource);
    }
    public int IndexOfRelated(int owner, int related, bool fromTargetToSource) => Array.IndexOf(list(lists(fromTargetToSource), owner), related);
    public IdSet Get(int id, bool fromTargetToSource) {
        var ids = list(lists(fromTargetToSource), id);
        if (ids.Length == 0) return IdSet.Empty;
        if (ids.Length == 1) return IdSet.SingleIdSet(ids[0]);
        var key = stateKey(id, fromTargetToSource);
        if (!_stateIds.TryGet(key, out var stateId)) {
            stateId = SetRegister.NewStateId();
            _stateIds.Set(key, stateId, 1);
        }
        return new IdSet(ids, stateId);
    }
    public IEnumerable<int> DistinctIds(bool fromTargetToSource) => lists(fromTargetToSource).Keys;
    public IEnumerable<RelData> Values => _edges.Entries.Select(e => new RelData((int)(e.Key >> 32), (int)(uint)e.Key, new DateTime(e.Value, DateTimeKind.Utc)));
    public int CountRelated(int id, bool fromTargetToSource) => list(lists(fromTargetToSource), id).Length;
    public int CountTarget(int source) => CountRelated(source, false);
    public int CountSource(int target) => CountRelated(target, true);
    public void DeleteIfReferenced(int id) {
        foreach (var target in list(_targetsBySource, id)) Remove(id, target);
        if (!IsSymmetric) foreach (var source in list(_sourcesByTarget, id)) Remove(source, id);
    }
    public IEnumerable<RelData> GetOtherRelationsThatNeedsToRemovedBeforeAdd(int source, int target) {
        switch (_type) {
            case RelationType.OneOne:
                foreach (var t in list(_targetsBySource, source)) yield return new RelData(source, t, GetDateTime(source, t));
                foreach (var s in list(_targetsBySource, target)) yield return new RelData(s, target, GetDateTime(s, target));
                break;
            case RelationType.OneToOne:
                foreach (var t in list(_targetsBySource, source)) yield return new RelData(source, t, GetDateTime(source, t));
                foreach (var s in list(_sourcesByTarget, target)) yield return new RelData(s, target, GetDateTime(s, target));
                break;
            case RelationType.OneToMany:
                foreach (var s in list(_sourcesByTarget, target)) yield return new RelData(s, target, GetDateTime(s, target));
                break;
        }
    }
    public void CompressMemory() { }
    public void SaveState(IAppendStream stream) { } // persisted by the engine
    public void ReadState(BufferReader stream) { }
}
