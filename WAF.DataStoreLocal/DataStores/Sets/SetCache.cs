using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using WAF.Common;
using static System.Runtime.InteropServices.JavaScript.JSType;

/// <summary>
/// The cache is based on a key value, that always will uniquely represent a set of ids.
/// The key will change if a set is changed. So there is not a risk that the cache will return a wrong set.
namespace WAF.DataStores.Sets;
internal class SetCacheKey {
    public SetCacheKey(SetOperation operationId, long[] stateIds, object[]? valueKeysMustBeValueType) {
        Operation = operationId;
        StateIds = stateIds;
        Values = valueKeysMustBeValueType;
        for (var i = 0; i < stateIds.Length; i++) {
            if (stateIds[i] == long.MaxValue) {
                NotCachable = true;
                break;
            }
        }
#if DEBUG
        _verifyValues(); // not strictly necessary, but just to make sure any code uses wrong key types
#endif
    }
    SetOperation Operation { get; }
    long[] StateIds { get; }
    object[]? Values { get; }
    public bool NotCachable;
    void _verifyValues() { // can be removed later
        if (Values != null) {
            foreach (var key in Values) {
                if (key is string) continue;
                if (key.GetType().IsValueType == false)
                    throw new ArgumentException("ValueKeys must be ValueTypes");
            }
        }
    }
    public override bool Equals(object? obj) {
        if (obj is not SetCacheKey other) return false;
        // return this.ToString() == other.ToString();
        if (Operation != other.Operation) return false;
        if (StateIds.Length != other.StateIds.Length) return false;
        if (Values == null && other.Values == null) {
            for (var i = 0; i < StateIds.Length; i++) {
                if (StateIds[i] != other.StateIds[i]) return false;
            }
            return true;
        } else if (Values != null && other.Values != null) {
            if (Values.Length != other.Values.Length) return false;
            for (var i = 0; i < StateIds.Length; i++) {
                if (StateIds[i] != other.StateIds[i]) return false;
            }
            for (var i = 0; i < Values.Length; i++) {
                if (!Values[i].Equals(other.Values[i]))
                    return false;
            }
            return true;
        } else {
            return false;
        }
    }
    public override string ToString() {
        return $"{Operation} on sets: {string.Join(", ", StateIds)} {(Values == null ? "" : " with values: " + string.Join(", ", Values.Cast<object>()))} ";
    }
    public override int GetHashCode() {
        //return this.ToString().GetHashCode();
        var hash = Operation.GetHashCode();
        foreach (var id in StateIds) hash ^= id.GetHashCode();
        if (Values != null) foreach (var key in Values) hash ^= key.GetHashCode();
        return hash;
    }
}
internal class StateIdTracker() {
    // state with logic to handle removal and re-addition of same value
    // clears cache when state changes
    private long _stateId = SetRegister.NewStateId();
    public long Current => _stateId;
    int _lastIdRemoved;
    long _lastStateIdBeforeRemove;
    public void RegisterAddition(int id) {
        if (_lastStateIdBeforeRemove > 0 && _lastIdRemoved == id) {
            // added same value for same id, that was just removed, so revert to old state id            
            _stateId = _lastStateIdBeforeRemove;
        } else {
            _stateId = SetRegister.NewStateId();
        }
        _lastStateIdBeforeRemove = 0;
    }
    public void RegisterRemoval(int id) {
        _lastIdRemoved = id;
        _lastStateIdBeforeRemove = _stateId;
        _stateId = SetRegister.NewStateId();
    }

    internal void Reset() {
        _lastStateIdBeforeRemove = 0;
        _lastIdRemoved = 0;
        _stateId = SetRegister.NewStateId();
    }
}
public class StateIdValueTracker<T>(SetRegister register) where T : notnull {
    // state with logic to handle removal and re-addition of same value
    private long _stateId = SetRegister.NewStateId();
    private readonly SetRegister _register = register;
    public long Current => _stateId;
    T? _lastValueRemoved;
    int _lastIdRemoved;
    long _lastStateIdBeforeRemove;
    public void RegisterAddition(int id, T value) {
        if (_lastStateIdBeforeRemove > 0 && _lastIdRemoved == id && _lastValueRemoved!.Equals(value)) {
            // added same value for same id, that was just removed, so revert to old state id            
            _stateId = _lastStateIdBeforeRemove;
        } else {
            _stateId = SetRegister.NewStateId();
        }
        _lastStateIdBeforeRemove = 0;
    }
    public void RegisterRemoval(int id, T value) {
        _lastIdRemoved = id;
        _lastValueRemoved = value;
        _lastStateIdBeforeRemove = _stateId;
        _stateId = SetRegister.NewStateId();
    }
}
internal class SetCache(long maxSize) : Cache<SetCacheKey, IdSet>(maxSize) { }
internal class AggregateCache(long maxSize) : Cache<SetCacheKey, int>(maxSize) { }

