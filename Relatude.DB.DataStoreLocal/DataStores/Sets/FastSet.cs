
using System.Collections;
using System.Collections.Frozen;
namespace Relatude.DB.DataStores.Sets;
public class FastSet {
    static public ISet<int> Create(ICollection<int> uniqueListOfIds) {
        // determine if we should use a bit set or a hash set ( both immutable )
        // a bit set is better if list is dense ( less mem and faster )
        var min = int.MaxValue;
        var max = int.MinValue;
        foreach (var id in uniqueListOfIds) {
            if (id < min) min = id;
            if (id > max) max = id;
        }
        var fractionEmpty = (1D + max - min) / uniqueListOfIds.Count;
        if (fractionEmpty > 2) {  // more than 2 empty slots per id on average
            return uniqueListOfIds.ToFrozenSet(); // use built in immutable hash set
        } else {
            return new bitSet(uniqueListOfIds, min, max);
        }
    }
    /// <summary>
    /// Immutable set of uints. Fast lookup and low memory
    private class bitSet : ISet<int> {
        readonly BitArray _bitArray;
        readonly int _min;
        readonly int _max;
        readonly int _length;
        public bitSet(ICollection<int> uniqueListOfIds, int min, int max) {
            _min = min;
            _max = max;
            Count = uniqueListOfIds.Count;
            _bitArray = new BitArray((max - min + 1));
            foreach (var id in uniqueListOfIds) {
                _bitArray.Set((id - min), true);
            }
            _length = _bitArray.Length;
        }
        public bool Contains(int id) => id >= _min && id <= _max && _bitArray.Get((id - _min));
        public int Count { get; }
        public bool IsReadOnly => true;
        public IEnumerator<int> GetEnumerator() {
            for (int i = 0; i < _length; i++) {
                if (_bitArray.Get(i)) yield return i + _min;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => throw new NotImplementedException();
        public void CopyTo(int[] array, int arrayIndex) { 
            if (array == null) throw new ArgumentNullException(nameof(array));
            if (arrayIndex < 0 || arrayIndex + Count > array.Length) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            int index = arrayIndex;
            for (int i = 0; i < _length; i++) {
                if (_bitArray.Get(i)) {
                    array[index++] = i + _min;
                }
            }
        }
        public void Add(int item) => throw new NotSupportedException("Read-only collection");
        public void Clear() => throw new NotSupportedException("Read-only collection");
        public bool Remove(int item) => throw new NotSupportedException("Read-only collection");

        bool ISet<int>.Add(int item) {
            throw new NotImplementedException();
        }

        public void ExceptWith(IEnumerable<int> other) {
            throw new NotImplementedException();
        }

        public void IntersectWith(IEnumerable<int> other) {
            throw new NotImplementedException();
        }

        public bool IsProperSubsetOf(IEnumerable<int> other) {
            throw new NotImplementedException();
        }

        public bool IsProperSupersetOf(IEnumerable<int> other) {
            throw new NotImplementedException();
        }

        public bool IsSubsetOf(IEnumerable<int> other) {
            throw new NotImplementedException();
        }

        public bool IsSupersetOf(IEnumerable<int> other) {
            throw new NotImplementedException();
        }

        public bool Overlaps(IEnumerable<int> other) {
            throw new NotImplementedException();
        }

        public bool SetEquals(IEnumerable<int> other) {
            throw new NotImplementedException();
        }

        public void SymmetricExceptWith(IEnumerable<int> other) {
            throw new NotImplementedException();
        }

        public void UnionWith(IEnumerable<int> other) {
            throw new NotImplementedException();
        }
    }
}
