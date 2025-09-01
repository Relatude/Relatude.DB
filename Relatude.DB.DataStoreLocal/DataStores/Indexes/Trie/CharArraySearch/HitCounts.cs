using Relatude.DB.Common;
namespace Relatude.DB.DataStores.Indexes.Trie.CharArraySearch;
class WordHitComparer : IEqualityComparer<WordHit> {
    public bool Equals(WordHit x, WordHit y) => x.NodeId == y.NodeId;
    public int GetHashCode(WordHit obj) => obj.NodeId.GetHashCode();
    public static IEqualityComparer<WordHit> Comparer = new WordHitComparer();
}
internal class HitCounts {
    const int _limitArray = 100; // for 1-100 items, use array, for more use HashSet
    WordHit _justRemoved = new(0, 0);
    object? _set;
    public HitCounts(WordHit item) {
        _set = item;
    }
    public HitCounts(WordHit[] items) {
        if (items.Length == 1) {
            _set = items[0];
        } else {
            _set = items; // always start with array, switch to hashset if required later
        }
    }
    public void Add(WordHit item) { // assume nodeId is not already in set!
        if (_justRemoved.NodeId == item.NodeId) {
            if (_justRemoved.Hits == item.Hits) {
                _justRemoved = new(0, 0);
            } else {
                if (_set != null) remove(_justRemoved.NodeId);
                add(item);
            }
        } else {
            add(item); // add the new item
        }
    }
    void add(WordHit item) { // assume nodeId is not already in set!
        if (_set is null) {
            _set = item;
        } else if (_set is WordHit one) {
            _set = new WordHit[] { one, item };
        } else if (_set is WordHit[] arr) {
            if (arr.Length >= _limitArray) {
                _set = new List<WordHit>(arr) { item };
            } else {
                var newArr = new WordHit[arr.Length + 1];
                Array.Copy(arr, newArr, arr.Length);
                newArr[arr.Length] = item;
                _set = newArr;
            }
        } else if (_set is ICollection<WordHit> coll) {
            coll.Add(item);
        } else {
            throw new Exception("Internal error"); // should never happen
        }
    }
    public void RemoveIfPresent(WordHit hit) {
        if(Count < 1) 
            return; // nothing to remove
        if (_justRemoved.NodeId == 0) {
            _justRemoved = hit;
        } else {
            remove(_justRemoved.NodeId);
            _justRemoved = hit;
        }
    }
    void remove(int nodeId) {
        if (_set is null) return;
        if (_set is WordHit one) {
            if (one.NodeId == nodeId) _set = null;
        } else if (_set is WordHit[] arr) {
            if (arr.Length > _limitArray) { // from initialization
                var hash = new HashSet<WordHit>(arr, WordHitComparer.Comparer);
                hash.Remove(new WordHit(nodeId, 0));
                _set = hash;
            } else {
                var newArr = arr.Where(i => i.NodeId != nodeId).ToArray();
                _set = newArr.Length == 1 ? newArr[0] : newArr;
            }
        } else if (_set is List<WordHit> list) {
            if (list.Count > _limitArray) { // from initialization
                var hash = new HashSet<WordHit>(list, WordHitComparer.Comparer);
                hash.Remove(new WordHit(nodeId, 0));
                _set = hash;
            } else {
                var newArr = list.Where(i => i.NodeId != nodeId).ToArray();
                _set = newArr.Length == 1 ? newArr[0] : newArr;
            }
        } else if (_set is HashSet<WordHit> hash) {
            hash.Remove(new WordHit(nodeId, 0));
            if (hash.Count == 1) _set = hash.First();
            else if (hash.Count < _limitArray) _set = hash.ToArray();
        } else {
            throw new Exception("Internal error: " + _set?.GetType().FullName + ".Remove not implemented");
        }
    }
    public int Count {
        get {
            var count = _set switch {
                WordHit => 1,
                WordHit[] arr => arr.Length,
                ICollection<WordHit> coll => coll.Count,
                _ => 0
            };
            if (_justRemoved.NodeId == 0) return count;
            return count - 1;
        }
    }
    void enforceDelayedRemove() {
        if (_justRemoved.NodeId == 0) return;
        remove(_justRemoved.NodeId);
        _justRemoved = new(0, 0);
    }
    public IEnumerable<WordHit> Values {
        get {
            enforceDelayedRemove();
            if (_set is null) yield break;
            else if (_set is WordHit item) yield return item;
            else if (_set is IEnumerable<WordHit> items) {
                foreach (var i in items) yield return i;
            } else {
                throw new NotImplementedException("GetItems not implemented");
            }
        }
    }
    public void Compress() {
        enforceDelayedRemove();
        if (_set is WordHit) return;
        if (_set is WordHit[] arr) {
            if (arr.Length == 1) _set = arr[0];
        } else if (_set is ICollection<WordHit> coll) {
            _set = coll.ToArray();
        }
    }
}

