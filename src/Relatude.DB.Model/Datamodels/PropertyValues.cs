using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.DB.Datamodels;

public struct PropertyEntry<T>(Guid key, T value) {
    public Guid PropertyId = key;
    public T Value = value;
}
public class Properties<T> {
    PropertyEntry<T>[] _values;
    int _size;
    public Properties(int sizeIndication) {
        _size = 0;
        _values = new PropertyEntry<T>[sizeIndication];
    }
    public Properties(Properties<T> properties) {
        _size = properties._size;
        _values = new PropertyEntry<T>[_size];
        Array.Copy(properties._values, _values, _size);
    }
    public void Add(Guid key, T v) {
        if (_size == _values.Length) {
            var increase = _values.Length == 0 ? 1 : _values.Length;
            Array.Resize(ref _values, _values.Length + increase); // double the size ( like lists )
        }
        _values[_size++] = new(key, v);
    }
    public bool ContainsKey(Guid key) {
        for (int i = 0; i < _size; i++) if (_values[i].PropertyId == key) return true;
        return false;
    }
    public void AddOrUpdate(Guid key, T value) {
        for (int i = 0; i < _size; i++) {
            if (_values[i].PropertyId == key) {
                _values[i] = new(key, value);
                return;
            }
        }
        Add(key, value);
    }
    public void RemoveIfPresent(Guid key) {
        for (int i = 0; i < _size; i++) {
            if (_values[i].PropertyId == key) {
                _values[i] = _values[--_size]; // move last item to this position
                return;
            }
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(Guid key, [MaybeNullWhen(false)] out T value) {
        for (int i = 0; i < _size; i++) {
            if (_values[i].PropertyId == key) {
                value = _values[i].Value;
                return true;
            }
        }
        value = default;
        return false;
    }
    public IEnumerable<PropertyEntry<T>> Items {
        get {
            for (int i = 0; i < _size; i++) yield return _values[i];
        }
    }
    public int Count => _size;
    public T this[Guid key] {
        get {
            for (int i = 0; i < _size; i++) if (_values[i].PropertyId == key) return _values[i].Value;
            throw new KeyNotFoundException("Property ID " + key + " was not found. ");
        }
        set {
            for (int i = 0; i < _size; i++) {
                if (_values[i].PropertyId == key) {
                    _values[i] = new(key, value);
                    return;
                }
            }
            Add(key, value);
        }
    }
}

//public class Properties<T> {
//    private Guid[] _keys;
//    private T[] _values;
//    private int _size;

//    public Properties(int sizeIndication) {
//        _size = 0;
//        // Optimization: Avoid 0-length arrays to skip initial resize logic
//        int initialCapacity = sizeIndication > 0 ? sizeIndication : 4;
//        _keys = new Guid[initialCapacity];
//        _values = new T[initialCapacity];
//    }

//    public Properties(Properties<T> properties) {
//        _size = properties._size;
//        _keys = new Guid[_size];
//        _values = new T[_size];
//        Array.Copy(properties._keys, _keys, _size);
//        Array.Copy(properties._values, _values, _size);
//    }

//    [MethodImpl(MethodImplOptions.AggressiveInlining)]
//    public bool TryGetValue(Guid key, [MaybeNullWhen(false)] out T value) {
//        // Cache _size in a local variable to help JIT with loop-invariant hoisting
//        int size = _size;
//        ReadOnlySpan<Guid> keys = _keys.AsSpan(0, size);

//        for (int i = 0; i < keys.Length; i++) {
//            if (keys[i] == key) {
//                value = _values[i];
//                return true;
//            }
//        }

//        value = default;
//        return false;
//    }

//    public void Add(Guid key, T v) {
//        if (_size == _keys.Length) {
//            // double the size (or start at 4)
//            int newSize = _keys.Length == 0 ? 4 : _keys.Length * 2;
//            Array.Resize(ref _keys, newSize);
//            Array.Resize(ref _values, newSize);
//        }
//        _keys[_size] = key;
//        _values[_size] = v;
//        _size++;
//    }

//    public bool ContainsKey(Guid key) {
//        ReadOnlySpan<Guid> keys = _keys.AsSpan(0, _size);
//        for (int i = 0; i < keys.Length; i++) {
//            if (keys[i] == key) return true;
//        }
//        return false;
//    }

//    public void AddOrUpdate(Guid key, T value) {
//        Span<Guid> keys = _keys.AsSpan(0, _size);
//        for (int i = 0; i < keys.Length; i++) {
//            if (keys[i] == key) {
//                _values[i] = value;
//                return;
//            }
//        }
//        Add(key, value);
//    }

//    public void RemoveIfPresent(Guid key) {
//        Span<Guid> keys = _keys.AsSpan(0, _size);
//        for (int i = 0; i < keys.Length; i++) {
//            if (keys[i] == key) {
//                int lastIdx = --_size;
//                // Move the last element into the hole (Order is not preserved)
//                _keys[i] = _keys[lastIdx];
//                _values[i] = _values[lastIdx];

//                // We must clear the last slot so the Garbage Collector can reclaim the object.
//                _keys[lastIdx] = default;
//                _values[lastIdx] = default!;
//                return;
//            }
//        }
//    }

//    public int Count => _size;

//    public T this[Guid key] {
//        get {
//            if (TryGetValue(key, out var value)) return value;
//            throw new KeyNotFoundException($"Property ID {key} was not found.");
//        }
//        set => AddOrUpdate(key, value);
//    }

//    // SIGNATURE PRESERVATION: Kept as IEnumerable to match your original API
//    public IEnumerable<PropertyEntry<T>> Items {
//        get {
//            for (int i = 0; i < _size; i++) {
//                yield return new PropertyEntry<T>(_keys[i], _values[i]);
//            }
//        }
//    }
//}

//public class Properties<T> {
//    Dictionary<Guid, T> _values;
//    public Properties(int sizeIndication) => _values = new Dictionary<Guid, T>(sizeIndication);
//    public Properties(Properties<T> properties) => _values = new Dictionary<Guid, T>(properties._values);
//    public void Add(Guid key, T v) => _values.Add(key, v);
//    public bool ContainsKey(Guid key) => _values.ContainsKey(key);
//    public void AddOrUpdate(Guid key, T value) => _values[key] = value;
//    public void RemoveIfPresent(Guid key) => _values.Remove(key);
//    public bool TryGetValue(Guid key, [MaybeNullWhen(false)] out T value) => _values.TryGetValue(key, out value);
//    public IEnumerable<PropertyEntry<T>> Items {
//        get {
//            foreach (var kv in _values) yield return new PropertyEntry<T>(kv.Key, kv.Value);
//        }
//    }
//    public int Count => _values.Count;
//    public T this[Guid key] {
//        get => _values[key];
//        set => _values[key] = value;
//    }
//}
