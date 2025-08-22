using System.Collections.Immutable;
using WAF.DataStores.Sets;
namespace WAF.DataStores.Relations;
public class RelatedList {
    // fixed order list with fast lookup and fast conversion to IdSet with StateId
    // also focus on using less memory
    static int maxBeforeHash = 100; // using hash for fewer items does not really make faster but use less mem
    readonly List<int> _list; // to facilitate fixed order
    HashSet<int>? _set; // to facilitate fast lookup
    IdSet? _idSet; // to facilitate fast conversion to IdSet
    public RelatedList(int id) {
        _list = [id];
    }
    public RelatedList() {
        _list = [];
    }
    public bool Contains(int key) => _set == null ? _list.Contains(key) : _set.Contains(key);
    public int Count => _list.Count; 
    public bool Remove(int key) {
        if (!Contains(key)) throw new ItemNotInRelationException();
        _list.Remove(key);
        if (_set != null) _set.Remove(key);
        if (_idSet != null) _idSet = null;
        return true;
    }
    public void Add(int key) {
        if (Contains(key)) throw new ItemAlreadyInRelationException();
        _list.Add(key);
        if (_set == null) {
            if (_list.Count > maxBeforeHash) _set = new(_list);
        } else {
            _set.Add(key);
        }
        if (_idSet != null) _idSet = null;
    }
    public IEnumerator<int> GetEnumerator() {
        return _list.GetEnumerator();
    }
    public IdSet ToIdSet() {
        if (_idSet == null) 
            _idSet = new(_list, SetRegister.NewStateId());
        return _idSet;
    }
}
