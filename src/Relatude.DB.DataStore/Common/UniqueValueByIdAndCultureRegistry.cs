using Relatude.DB.IO;
using System.Runtime.CompilerServices;
namespace Relatude.DB.Common;

public abstract class UniqueValueByIdAndCultureRegistry<T> where T : notnull {
    private readonly Dictionary<long, T> _valueByIdAndCulture = new();
    private readonly Dictionary<T, long> _idAndCultureByValue;
    private readonly Dictionary<string, byte> _cultureIdByCode = new(StringComparer.Ordinal);
    private readonly string?[] _cultureCodeById = new string[256];
    private readonly IEqualityComparer<T> _valueComparer;
    private readonly Func<T, int, T>? _conflictResolver;
    private byte _lastCultureId = 0;
    private bool _inTransaction;
    private byte _transactionStartCultureId;
    private List<UndoEntry>? _undoLog;

    private enum UndoKind : byte {
        RestoreValueByIdAndCulture,
        RestoreIdAndCultureByValue,
    }

    private readonly struct UndoEntry {
        public readonly UndoKind Kind;
        public readonly long Key;
        public readonly T? Value;
        public readonly bool HadValue;

        public UndoEntry(UndoKind kind, long key, T? value, bool hadValue) {
            Kind = kind;
            Key = key;
            Value = value;
            HadValue = hadValue;
        }
    }

    public UniqueValueByIdAndCultureRegistry(IEqualityComparer<T>? valueComparer = null, Func<T, int, T>? conflictResolver = null) {
        _valueComparer = valueComparer ?? EqualityComparer<T>.Default;
        _idAndCultureByValue = new Dictionary<T, long>(_valueComparer);
        _conflictResolver = conflictResolver ?? CreateDefaultConflictResolver();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackKey(int id, byte cultureId) {
        return ((long)(uint)id << 8) | cultureId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int UnpackId(long key) {
        return unchecked((int)(uint)(key >> 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte UnpackCultureId(long key) {
        return (byte)key;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetPositiveIntDigitCount(int value) {
        if (value < 10) return 1;
        if (value < 100) return 2;
        if (value < 1_000) return 3;
        if (value < 10_000) return 4;
        if (value < 100_000) return 5;
        if (value < 1_000_000) return 6;
        if (value < 10_000_000) return 7;
        if (value < 100_000_000) return 8;
        if (value < 1_000_000_000) return 9;
        return 10;
    }

    private static string CreateSuffixedString(string value, int suffix) {
        int digits = GetPositiveIntDigitCount(suffix);
        return string.Create(value.Length + 1 + digits, (value, suffix), static (span, state) => {
            state.value.AsSpan().CopyTo(span);
            int offset = state.value.Length;
            span[offset] = '-';
            state.suffix.TryFormat(span[(offset + 1)..], out _);
        });
    }

    private static Func<T, int, T>? CreateDefaultConflictResolver() {
        if (typeof(T) == typeof(string)) {
            return static (value, suffix) => (T)(object)CreateSuffixedString((string)(object)value!, suffix);
        }

        return null;
    }

    private bool TryGetCultureId(string? cultureCode, out byte cultureId) {
        if (cultureCode is null) {
            cultureId = 0;
            return true;
        }

        return _cultureIdByCode.TryGetValue(cultureCode, out cultureId);
    }
    private byte GetOrAddCultureId(string? cultureCode) {
        if (cultureCode is null) {
            return 0;
        }

        if (_cultureIdByCode.TryGetValue(cultureCode, out var cultureId)) {
            return cultureId;
        }

        if (_lastCultureId == byte.MaxValue) {
            throw new InvalidOperationException("Registry supports up to 255 distinct non-null culture codes.");
        }

        _lastCultureId++;
        cultureId = _lastCultureId;
        _cultureIdByCode[cultureCode] = cultureId;
        _cultureCodeById[cultureId] = cultureCode;
        return cultureId;
    }

    public void BeginTransaction() {
        if (_inTransaction) {
            throw new InvalidOperationException("Transaction already started.");
        }

        _inTransaction = true;
        _transactionStartCultureId = _lastCultureId;
        if (_undoLog is null) {
            _undoLog = new List<UndoEntry>(32);
        } else {
            _undoLog.Clear();
        }
    }
    public void CommitTransaction() {
        if (!_inTransaction) {
            return;
        }

        _undoLog?.Clear();
        _inTransaction = false;
    }
    public void RollbackTransaction() {
        if (!_inTransaction) {
            return;
        }

        var undoLog = _undoLog;
        _inTransaction = false;

        if (undoLog is not null) {
            for (int i = undoLog.Count - 1; i >= 0; i--) {
                var entry = undoLog[i];
                switch (entry.Kind) {
                    case UndoKind.RestoreValueByIdAndCulture:
                        if (entry.HadValue) {
                            _valueByIdAndCulture[entry.Key] = entry.Value!;
                        } else {
                            _valueByIdAndCulture.Remove(entry.Key);
                        }
                        break;
                    case UndoKind.RestoreIdAndCultureByValue:
                        if (entry.HadValue) {
                            _idAndCultureByValue[entry.Value!] = entry.Key;
                        } else {
                            _idAndCultureByValue.Remove(entry.Value!);
                        }
                        break;
                    default:
                        throw new InvalidOperationException("Unknown undo operation.");
                }
            }
        }

        for (int i = _lastCultureId; i > _transactionStartCultureId; i--) {
            var cultureCode = _cultureCodeById[i];
            if (cultureCode is not null) {
                _cultureIdByCode.Remove(cultureCode);
                _cultureCodeById[i] = null;
            }
        }

        _lastCultureId = _transactionStartCultureId;
        _undoLog?.Clear();
    }

    private void SetValueByIdAndCulture(long key, T value) {
        if (_inTransaction && _undoLog is not null) {
            if (_valueByIdAndCulture.TryGetValue(key, out var existing)) {
                _undoLog.Add(new UndoEntry(UndoKind.RestoreValueByIdAndCulture, key, existing, true));
            } else {
                _undoLog.Add(new UndoEntry(UndoKind.RestoreValueByIdAndCulture, key, default, false));
            }
        }

        _valueByIdAndCulture[key] = value;
    }

    private void RemoveValueByIdAndCulture(long key) {
        if (!_valueByIdAndCulture.TryGetValue(key, out var existing)) {
            return;
        }

        if (_inTransaction && _undoLog is not null) {
            _undoLog.Add(new UndoEntry(UndoKind.RestoreValueByIdAndCulture, key, existing, true));
        }

        _valueByIdAndCulture.Remove(key);
    }

    private void SetIdAndCultureByValue(T value, long key) {
        if (_inTransaction && _undoLog is not null) {
            if (_idAndCultureByValue.TryGetValue(value, out var existing)) {
                _undoLog.Add(new UndoEntry(UndoKind.RestoreIdAndCultureByValue, existing, value, true));
            } else {
                _undoLog.Add(new UndoEntry(UndoKind.RestoreIdAndCultureByValue, default, value, false));
            }
        }

        _idAndCultureByValue[value] = key;
    }

    private void RemoveIdAndCultureByValue(T value) {
        if (!_idAndCultureByValue.TryGetValue(value, out var existing)) {
            return;
        }

        if (_inTransaction && _undoLog is not null) {
            _undoLog.Add(new UndoEntry(UndoKind.RestoreIdAndCultureByValue, existing, value, true));
        }

        _idAndCultureByValue.Remove(value);
    }

    public bool TryGetId(T value, out int id, out string? cultureCode) {
        if (_idAndCultureByValue.TryGetValue(value, out var key)) {
            id = UnpackId(key);
            cultureCode = _cultureCodeById[UnpackCultureId(key)];
            return true;
        }

        id = 0;
        cultureCode = null;
        return false;
    }
    public T? GetValue(int id, string? cultureCode) {
        if (!TryGetCultureId(cultureCode, out var cultureId)) {
            return default;
        }

        return _valueByIdAndCulture.TryGetValue(PackKey(id, cultureId), out var value)
            ? value
            : default;
    }
    public void Update(int id, T? value, string? cultureCode, out T? newValue, out bool changedNewValue) {
        byte cultureId;
        if (value is null) {
            if (!TryGetCultureId(cultureCode, out cultureId)) {
                newValue = default;
                changedNewValue = false;
                return;
            }
        } else {
            cultureId = GetOrAddCultureId(cultureCode);
        }

        var key = PackKey(id, cultureId);
        _valueByIdAndCulture.TryGetValue(key, out var currentValue);

        if (value is null) {
            if (currentValue is not null) {
                RemoveValueByIdAndCulture(key);
                RemoveIdAndCultureByValue(currentValue);
            }

            newValue = default;
            changedNewValue = false;
            return;
        }

        var candidate = value;
        if (_idAndCultureByValue.TryGetValue(candidate, out var owner) && owner != key) {
            if (_conflictResolver is null) {
                throw new InvalidOperationException($"Unable to resolve duplicate values for '{typeof(T).Name}'. Provide a conflictResolver in the constructor.");
            }

            var attempt = 2;
            while (true) {
                candidate = _conflictResolver(value, attempt);
                if (!_idAndCultureByValue.TryGetValue(candidate, out owner) || owner == key) {
                    break;
                }

                attempt++;
            }
        }

        changedNewValue = !_valueComparer.Equals(candidate, value);

        if (currentValue is not null && _valueComparer.Equals(currentValue, candidate)) {
            newValue = candidate;
            return;
        }

        if (currentValue is not null) {
            RemoveIdAndCultureByValue(currentValue);
        }

        SetValueByIdAndCulture(key, candidate);
        SetIdAndCultureByValue(candidate, key);
        newValue = candidate;
    }
    public void Remove(int id, T? value, string? cultureCode) {
        Update(id, default, cultureCode, out _, out _);
    }

    public void SaveState(IAppendStream stream) {
    }
    public void ReadState(IReadStream stream) {
    }

}
public abstract class ValueByIdAndCultureRegistry<T> where T : notnull {
    private readonly Dictionary<long, T> _valueByIdAndCulture = new();
    private readonly Dictionary<T, HashSet<long>> _keysByValue;
    private readonly Dictionary<string, byte> _cultureIdByCode = new(StringComparer.Ordinal);
    private readonly string?[] _cultureCodeById = new string[256];
    private readonly IEqualityComparer<T> _valueComparer;
    private byte _lastCultureId = 0;
    private bool _inTransaction;
    private byte _transactionStartCultureId;
    private List<UndoEntry>? _undoLog;

    private readonly struct UndoEntry {
        public readonly long Key;
        public readonly T? Value;
        public readonly bool HadValue;

        public UndoEntry(long key, T? value, bool hadValue) {
            Key = key;
            Value = value;
            HadValue = hadValue;
        }
    }

    public ValueByIdAndCultureRegistry(IEqualityComparer<T>? valueComparer = null) {
        _valueComparer = valueComparer ?? EqualityComparer<T>.Default;
        _keysByValue = new Dictionary<T, HashSet<long>>(_valueComparer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackKey(int id, byte cultureId) {
        return ((long)(uint)id << 8) | cultureId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int UnpackId(long key) {
        return unchecked((int)(uint)(key >> 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte UnpackCultureId(long key) {
        return (byte)key;
    }

    private bool TryGetCultureId(string? cultureCode, out byte cultureId) {
        if (cultureCode is null) {
            cultureId = 0;
            return true;
        }

        return _cultureIdByCode.TryGetValue(cultureCode, out cultureId);
    }

    private byte GetOrAddCultureId(string? cultureCode) {
        if (cultureCode is null) {
            return 0;
        }

        if (_cultureIdByCode.TryGetValue(cultureCode, out var cultureId)) {
            return cultureId;
        }

        if (_lastCultureId == byte.MaxValue) {
            throw new InvalidOperationException("Registry supports up to 255 distinct non-null culture codes.");
        }

        _lastCultureId++;
        cultureId = _lastCultureId;
        _cultureIdByCode[cultureCode] = cultureId;
        _cultureCodeById[cultureId] = cultureCode;
        return cultureId;
    }

    public void BeginTransaction() {
        if (_inTransaction) {
            throw new InvalidOperationException("Transaction already started.");
        }

        _inTransaction = true;
        _transactionStartCultureId = _lastCultureId;
        if (_undoLog is null) {
            _undoLog = new List<UndoEntry>(32);
        } else {
            _undoLog.Clear();
        }
    }

    public void CommitTransaction() {
        if (!_inTransaction) {
            return;
        }

        _undoLog?.Clear();
        _inTransaction = false;
    }

    public void RollbackTransaction() {
        if (!_inTransaction) {
            return;
        }

        var undoLog = _undoLog;
        _inTransaction = false;

        if (undoLog is not null) {
            for (int i = undoLog.Count - 1; i >= 0; i--) {
                var entry = undoLog[i];
                if (entry.HadValue) {
                    _valueByIdAndCulture[entry.Key] = entry.Value!;
                } else {
                    _valueByIdAndCulture.Remove(entry.Key);
                }
            }
        }

        for (int i = _lastCultureId; i > _transactionStartCultureId; i--) {
            var cultureCode = _cultureCodeById[i];
            if (cultureCode is not null) {
                _cultureIdByCode.Remove(cultureCode);
                _cultureCodeById[i] = null;
            }
        }

        _lastCultureId = _transactionStartCultureId;
        _undoLog?.Clear();
        RebuildValueIndex();
    }

    private void AddUndo(long key) {
        if (!_inTransaction || _undoLog is null) {
            return;
        }

        if (_valueByIdAndCulture.TryGetValue(key, out var existing)) {
            _undoLog.Add(new UndoEntry(key, existing, true));
        } else {
            _undoLog.Add(new UndoEntry(key, default, false));
        }
    }

    private void AddKeyToValueIndex(T value, long key) {
        if (!_keysByValue.TryGetValue(value, out var keys)) {
            keys = new HashSet<long>();
            _keysByValue[value] = keys;
        }

        keys.Add(key);
    }

    private void RemoveKeyFromValueIndex(T value, long key) {
        if (!_keysByValue.TryGetValue(value, out var keys)) {
            return;
        }

        keys.Remove(key);
        if (keys.Count == 0) {
            _keysByValue.Remove(value);
        }
    }

    private void RebuildValueIndex() {
        _keysByValue.Clear();
        foreach (var pair in _valueByIdAndCulture) {
            AddKeyToValueIndex(pair.Value, pair.Key);
        }
    }

    private void SetValueByIdAndCulture(long key, T value) {
        AddUndo(key);

        if (_valueByIdAndCulture.TryGetValue(key, out var existing)) {
            if (_valueComparer.Equals(existing, value)) {
                return;
            }

            RemoveKeyFromValueIndex(existing, key);
        }

        _valueByIdAndCulture[key] = value;
        AddKeyToValueIndex(value, key);
    }

    private void RemoveValueByIdAndCulture(long key) {
        if (!_valueByIdAndCulture.TryGetValue(key, out var existing)) {
            return;
        }

        AddUndo(key);
        _valueByIdAndCulture.Remove(key);
        RemoveKeyFromValueIndex(existing, key);
    }

    public bool TryGetId(T value, out int id, out string? cultureCode) {
        if (_keysByValue.TryGetValue(value, out var keys)) {
            foreach (var key in keys) {
                id = UnpackId(key);
                cultureCode = _cultureCodeById[UnpackCultureId(key)];
                return true;
            }
        }

        id = 0;
        cultureCode = null;
        return false;
    }

    public T? GetValue(int id, string? cultureCode) {
        if (!TryGetCultureId(cultureCode, out var cultureId)) {
            return default;
        }

        return _valueByIdAndCulture.TryGetValue(PackKey(id, cultureId), out var value)
            ? value
            : default;
    }

    public int GetValueUsageCount(T value) {
        return _keysByValue.TryGetValue(value, out var keys) ? keys.Count : 0;
    }

    public void Update(int id, T? value, string? cultureCode, out T? newValue, out bool changedNewValue) {
        byte cultureId;
        if (value is null) {
            if (!TryGetCultureId(cultureCode, out cultureId)) {
                newValue = default;
                changedNewValue = false;
                return;
            }
        } else {
            cultureId = GetOrAddCultureId(cultureCode);
        }

        var key = PackKey(id, cultureId);
        _valueByIdAndCulture.TryGetValue(key, out var currentValue);

        if (value is null) {
            if (currentValue is not null) {
                RemoveValueByIdAndCulture(key);
            }

            newValue = default;
            changedNewValue = false;
            return;
        }

        changedNewValue = false;

        if (currentValue is not null && _valueComparer.Equals(currentValue, value)) {
            newValue = currentValue;
            return;
        }

        SetValueByIdAndCulture(key, value);
        newValue = value;
    }

    public void Remove(int id, string? cultureCode) {
        Update(id, default, cultureCode, out _, out _);
    }

    public void SaveState(IAppendStream stream) {
    }

    public void ReadState(IReadStream stream) {
    }
}

public class AddressRegistry : UniqueValueByIdAndCultureRegistry<string> {
    public AddressRegistry()
        : base(StringComparer.OrdinalIgnoreCase) {
    }

    public AddressRegistry(Func<string, int, string>? conflictResolver)
        : base(StringComparer.OrdinalIgnoreCase, conflictResolver) {
    }

}

public class DisplayNameRegistry : ValueByIdAndCultureRegistry<string> {
    public DisplayNameRegistry()
        : base(StringComparer.Ordinal) {
    }
}

