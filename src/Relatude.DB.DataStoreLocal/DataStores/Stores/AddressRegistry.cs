using Relatude.DB.IO;
using System.Runtime.CompilerServices;

namespace Relatude.DB.DataStores.Stores;

public class AddressRegistry {
    private readonly Dictionary<long, string> _addressByIdAndCulture = new();
    private readonly Dictionary<string, long> _idAndCultureByAddress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte> _cultureIdByCode = new(StringComparer.Ordinal);
    private readonly string?[] _cultureCodeById = new string[256];
    private byte _lastCultureId = 0;
    private bool _inTransaction;
    private byte _transactionStartCultureId;
    private List<UndoEntry>? _undoLog;

    private enum UndoKind : byte {
        RestoreAddressByIdAndCulture,
        RestoreIdAndCultureByAddress,
    }

    private readonly struct UndoEntry {
        public readonly UndoKind Kind;
        public readonly long Key;
        public readonly string Address;
        public readonly bool HadValue;

        public UndoEntry(UndoKind kind, long key, string address, bool hadValue) {
            Kind = kind;
            Key = key;
            Address = address;
            HadValue = hadValue;
        }
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

    private static string CreateSuffixedAddress(string address, int suffix) {
        int digits = GetPositiveIntDigitCount(suffix);
        return string.Create(address.Length + 1 + digits, (address, suffix), static (span, state) => {
            state.address.AsSpan().CopyTo(span);
            int offset = state.address.Length;
            span[offset] = '-';
            state.suffix.TryFormat(span[(offset + 1)..], out _);
        });
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
            throw new InvalidOperationException("AddressRegistry supports up to 255 distinct non-null culture codes.");
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
                    case UndoKind.RestoreAddressByIdAndCulture:
                        if (entry.HadValue) {
                            _addressByIdAndCulture[entry.Key] = entry.Address;
                        } else {
                            _addressByIdAndCulture.Remove(entry.Key);
                        }
                        break;
                    case UndoKind.RestoreIdAndCultureByAddress:
                        if (entry.HadValue) {
                            _idAndCultureByAddress[entry.Address] = entry.Key;
                        } else {
                            _idAndCultureByAddress.Remove(entry.Address);
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

    private void SetAddressByIdAndCulture(long key, string value) {
        if (_inTransaction && _undoLog is not null) {
            if (_addressByIdAndCulture.TryGetValue(key, out var existing)) {
                _undoLog.Add(new UndoEntry(UndoKind.RestoreAddressByIdAndCulture, key, existing, true));
            } else {
                _undoLog.Add(new UndoEntry(UndoKind.RestoreAddressByIdAndCulture, key, string.Empty, false));
            }
        }

        _addressByIdAndCulture[key] = value;
    }

    private void RemoveAddressByIdAndCulture(long key) {
        if (!_addressByIdAndCulture.TryGetValue(key, out var existing)) {
            return;
        }

        if (_inTransaction && _undoLog is not null) {
            _undoLog.Add(new UndoEntry(UndoKind.RestoreAddressByIdAndCulture, key, existing, true));
        }

        _addressByIdAndCulture.Remove(key);
    }

    private void SetIdAndCultureByAddress(string address, long key) {
        if (_inTransaction && _undoLog is not null) {
            if (_idAndCultureByAddress.TryGetValue(address, out var existing)) {
                _undoLog.Add(new UndoEntry(UndoKind.RestoreIdAndCultureByAddress, existing, address, true));
            } else {
                _undoLog.Add(new UndoEntry(UndoKind.RestoreIdAndCultureByAddress, default, address, false));
            }
        }

        _idAndCultureByAddress[address] = key;
    }

    private void RemoveIdAndCultureByAddress(string address) {
        if (!_idAndCultureByAddress.TryGetValue(address, out var existing)) {
            return;
        }

        if (_inTransaction && _undoLog is not null) {
            _undoLog.Add(new UndoEntry(UndoKind.RestoreIdAndCultureByAddress, existing, address, true));
        }

        _idAndCultureByAddress.Remove(address);
    }

    public bool TryGetId(string address, out int id, out string? cultureCode) {
        if (_idAndCultureByAddress.TryGetValue(address, out var key)) {
            id = UnpackId(key);
            cultureCode = _cultureCodeById[UnpackCultureId(key)];
            return true;
        }

        id = 0;
        cultureCode = null;
        return false;
    }
    public string? GetAddress(int id, string? cultureCode) {
        if (!TryGetCultureId(cultureCode, out var cultureId)) {
            return null;
        }

        return _addressByIdAndCulture.TryGetValue(PackKey(id, cultureId), out var address)
            ? address
            : null;
    }
    public void Update(int id, string? address, string? cultureCode, out string? newAddress, out bool changedNewAddress) {
        byte cultureId;
        if (address is null) {
            if (!TryGetCultureId(cultureCode, out cultureId)) {
                newAddress = null;
                changedNewAddress = false;
                return;
            }
        } else {
            cultureId = GetOrAddCultureId(cultureCode);
        }

        var key = PackKey(id, cultureId);
        _addressByIdAndCulture.TryGetValue(key, out var currentAddress);

        if (address is null) {
            if (currentAddress is not null) {
                RemoveAddressByIdAndCulture(key);
                RemoveIdAndCultureByAddress(currentAddress);
            }

            newAddress = null;
            changedNewAddress = false;
            return;
        }

        var candidate = address;
        if (_idAndCultureByAddress.TryGetValue(candidate, out var owner) && owner != key) {
            var suffix = 2;
            while (true) {
                candidate = CreateSuffixedAddress(address, suffix);
                if (!_idAndCultureByAddress.TryGetValue(candidate, out owner) || owner == key) {
                    break;
                }

                suffix++;
            }
        }

        changedNewAddress = !string.Equals(candidate, address, StringComparison.Ordinal);

        if (string.Equals(currentAddress, candidate, StringComparison.Ordinal)) {
            newAddress = candidate;
            return;
        }

        if (currentAddress is not null) {
            RemoveIdAndCultureByAddress(currentAddress);
        }

        SetAddressByIdAndCulture(key, candidate);
        SetIdAndCultureByAddress(candidate, key);
        newAddress = candidate;
    }
    public void Remove(int id, string? address, string? cultureCode) {
        Update(id, null, cultureCode, out _, out _);
    }

    public void SaveState(IAppendStream stream) {
    }
    public void ReadState(IReadStream stream) {
    }

}




//using System.Collections.Generic;

//namespace Relatude.DB.DataStores.Stores;

//public class AddressRegistry {
//    private readonly Dictionary<IdCultureKey, string> _addressByIdAndCulture = new(IdCultureKeyComparer.Instance);
//    private readonly Dictionary<string, IdCultureKey> _idAndCultureByAddress = new(StringComparer.Ordinal);

//    private readonly struct IdCultureKey {
//        public readonly int Id;
//        public readonly string? CultureCode;

//        public IdCultureKey(int id, string? cultureCode) {
//            Id = id;
//            CultureCode = cultureCode;
//        }
//    }

//    private sealed class IdCultureKeyComparer : IEqualityComparer<IdCultureKey> {
//        public static readonly IdCultureKeyComparer Instance = new();

//        public bool Equals(IdCultureKey x, IdCultureKey y) {
//            return x.Id == y.Id && string.Equals(x.CultureCode, y.CultureCode, StringComparison.Ordinal);
//        }

//        public int GetHashCode(IdCultureKey obj) {
//            return HashCode.Combine(obj.Id, obj.CultureCode is null ? 0 : StringComparer.Ordinal.GetHashCode(obj.CultureCode));
//        }
//    }

//    /// <summary>
//    /// Tries to get the id and culture associated with the given address and culture. Returns false if not found.
//    /// If false it means the address was not found, and the out parameters will be set to default values (id = 0, culture = null).
//    /// </summary>
//    /// <param name="address">The node address</param>
//    /// <param name="id">The node id</param>
//    /// <param name="cultureCode">Culture == null is a valid value </param>
//    /// <returns>true if the address was found and the id and culture were returned in the out parameters; otherwise, false.</returns>
//    public bool TryGetId(string address, out int id, out string? cultureCode) {
//        if (_idAndCultureByAddress.TryGetValue(address, out var key)) {
//            id = key.Id;
//            cultureCode = key.CultureCode;
//            return true;
//        }

//        id = 0;
//        cultureCode = null;
//        return false;
//    }
//    /// <summary>
//    /// Retrieves the address associated with the specified identifier for a given culture.
//    /// If no address is found the method returns null.
//    /// </summary>
//    /// <param name="id">The unique identifier of the address to retrieve.</param>
//    /// <param name="cultureCode">An optional culture code (such as "en-US") used to localize the address. If null, the default culture is used.</param>
//    /// <returns>A string containing the address if found; otherwise, null.</returns>
//    public string? GetAddress(int id, string? cultureCode) {
//        return _addressByIdAndCulture.TryGetValue(new IdCultureKey(id, cultureCode), out var address)
//            ? address
//            : null;
//    }
//    /// <summary>
//    /// Updates the address information for the specified entity and indicates whether the address was changed.
//    /// If the provided address is null, it removes the existing address for the given id and culture.
//    /// If the address is currently in use by another id and culture, suggest a new unique address by appending a suffix to the provided address until it is unique, and return the new address in the out parameter. The method also returns a boolean indicating whether the address was changed (either updated to a new unique address or deleted if null was provided).
//    /// </summary>
//    /// <param name="id">The unique identifier of the entity whose address is to be updated.</param>
//    /// <param name="address">The new address to assign to the entity. If null, the address is deleted for the id and culture</param>
//    /// <param name="cultureCode">An optional culture code that specifies the localization context for the address update</param>
//    /// <param name="newAddress">When this method returns, contains the updated address value. This parameter is passed uninitialized.</param>
//    /// <param name="changedNewAddress">When this method returns, contains <see langword="true"/> if the given address had to be changed for uniqueness; otherwise, <see langword="false"/>.</param>
//    public void Update(int id, string? address, string? cultureCode, out string? newAddress, out bool changedNewAddress) {
//        var key = new IdCultureKey(id, cultureCode);
//        _addressByIdAndCulture.TryGetValue(key, out var currentAddress);

//        if (address is null) {
//            if (currentAddress is not null) {
//                _addressByIdAndCulture.Remove(key);
//                _idAndCultureByAddress.Remove(currentAddress);
//            }

//            newAddress = null;
//            changedNewAddress = false;
//            return;
//        }

//        var candidate = address;
//        if (_idAndCultureByAddress.TryGetValue(candidate, out var owner)
//            && (owner.Id != key.Id || !string.Equals(owner.CultureCode, key.CultureCode, StringComparison.Ordinal))) {
//            var suffix = 2;
//            while (true) {
//                candidate = string.Concat(address, "-", suffix.ToString());
//                if (!_idAndCultureByAddress.TryGetValue(candidate, out owner)
//                    || (owner.Id == key.Id && string.Equals(owner.CultureCode, key.CultureCode, StringComparison.Ordinal))) {
//                    break;
//                }

//                suffix++;
//            }
//        }

//        changedNewAddress = !string.Equals(candidate, address, StringComparison.Ordinal);

//        if (string.Equals(currentAddress, candidate, StringComparison.Ordinal)) {
//            newAddress = candidate;
//            return;
//        }

//        if (currentAddress is not null) {
//            _idAndCultureByAddress.Remove(currentAddress);
//        }

//        _addressByIdAndCulture[key] = candidate;
//        _idAndCultureByAddress[candidate] = key;
//        newAddress = candidate;
//    }
//    public void Remove(int id, string? address, string? cultureCode) {
//        Update(id, null, cultureCode, out _, out _);
//    }
//}