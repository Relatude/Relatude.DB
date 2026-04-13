using Relatude.DB.IO;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Relatude.DB.DataStores.Stores;

public class AddressRegistry {
    private static readonly Guid _marker = new("fa5f4dd3-8520-4fc9-a260-637fe9ddb2ca");
    private readonly Dictionary<long, string> _addressByIdAndCulture = new();
    private readonly Dictionary<string, long> _idAndCultureByAddress = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, byte> _cultureIdByCode = new();
    private readonly Guid?[] _cultureCodeById = new Guid?[256];
    private byte _lastCultureId = 0;
    private bool _inTransaction;
    private byte _transactionStartCultureId;
    private List<undoEntry>? _undoLog;

    enum undoKind : byte {
        RestoreAddressByIdAndCulture,
        RestoreIdAndCultureByAddress,
    }

    readonly struct undoEntry {
        public readonly undoKind Kind;
        public readonly long Key;
        public readonly string Address;
        public readonly bool HadValue;

        public undoEntry(undoKind kind, long key, string address, bool hadValue) {
            Kind = kind;
            Key = key;
            Address = address;
            HadValue = hadValue;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static long packKey(int id, byte cultureId) {
        return ((long)(uint)id << 8) | cultureId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int unpackId(long key) {
        return unchecked((int)(uint)(key >> 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static byte unpackCultureId(long key) {
        return (byte)key;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int getPositiveIntDigitCount(int value) {
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

    static string createSuffixedAddress(string address, int suffix) {
        int digits = getPositiveIntDigitCount(suffix);
        return string.Create(address.Length + 1 + digits, (address, suffix), static (span, state) => {
            state.address.AsSpan().CopyTo(span);
            int offset = state.address.Length;
            span[offset] = '-';
            state.suffix.TryFormat(span[(offset + 1)..], out _);
        });
    }

    bool tryGetCultureId(Guid? cultureCode, out byte cultureId) {
        if (!cultureCode.HasValue || cultureCode.Value == Guid.Empty) {
            cultureId = 0;
            return true;
        }

        return _cultureIdByCode.TryGetValue(cultureCode.Value, out cultureId);
    }
    byte getOrAddCultureId(Guid? cultureCode) {
        if (!cultureCode.HasValue || cultureCode.Value == Guid.Empty) {
            return 0;
        }

        var cultureGuid = cultureCode.Value;
        if (_cultureIdByCode.TryGetValue(cultureGuid, out var cultureId)) {
            return cultureId;
        }

        if (_lastCultureId == byte.MaxValue) {
            throw new InvalidOperationException("AddressRegistry supports up to 255 distinct non-empty culture ids.");
        }

        _lastCultureId++;
        cultureId = _lastCultureId;
        _cultureIdByCode[cultureGuid] = cultureId;
        _cultureCodeById[cultureId] = cultureGuid;
        return cultureId;
    }

    void setAddressByIdAndCulture(long key, string value) {
        if (_inTransaction && _undoLog is not null) {
            if (_addressByIdAndCulture.TryGetValue(key, out var existing)) {
                _undoLog.Add(new undoEntry(undoKind.RestoreAddressByIdAndCulture, key, existing, true));
            } else {
                _undoLog.Add(new undoEntry(undoKind.RestoreAddressByIdAndCulture, key, string.Empty, false));
            }
        }

        _addressByIdAndCulture[key] = value;
    }

    void removeAddressByIdAndCulture(long key) {
        if (!_addressByIdAndCulture.TryGetValue(key, out var existing)) {
            return;
        }

        if (_inTransaction && _undoLog is not null) {
            _undoLog.Add(new undoEntry(undoKind.RestoreAddressByIdAndCulture, key, existing, true));
        }

        _addressByIdAndCulture.Remove(key);
    }

    private void setIdAndCultureByAddress(string address, long key) {
        if (_inTransaction && _undoLog is not null) {
            if (_idAndCultureByAddress.TryGetValue(address, out var existing)) {
                _undoLog.Add(new undoEntry(undoKind.RestoreIdAndCultureByAddress, existing, address, true));
            } else {
                _undoLog.Add(new undoEntry(undoKind.RestoreIdAndCultureByAddress, default, address, false));
            }
        }

        _idAndCultureByAddress[address] = key;
    }

    private void removeIdAndCultureByAddress(string address) {
        if (!_idAndCultureByAddress.TryGetValue(address, out var existing)) {
            return;
        }

        if (_inTransaction && _undoLog is not null) {
            _undoLog.Add(new undoEntry(undoKind.RestoreIdAndCultureByAddress, existing, address, true));
        }

        _idAndCultureByAddress.Remove(address);
    }


    public void BeginTransaction() {
        if (_inTransaction) {
            throw new InvalidOperationException("Transaction already started.");
        }

        _inTransaction = true;
        _transactionStartCultureId = _lastCultureId;
        if (_undoLog is null) {
            _undoLog = new List<undoEntry>(32);
        } else {
            _undoLog.Clear();
        }
    }
    public void Commit() {
        if (!_inTransaction) {
            return;
        }

        _undoLog?.Clear();
        _inTransaction = false;
    }
    public void RollbackIfUncommited() {
        if (!_inTransaction) {
            return;
        }

        var undoLog = _undoLog;
        _inTransaction = false;

        if (undoLog is not null) {
            for (int i = undoLog.Count - 1; i >= 0; i--) {
                var entry = undoLog[i];
                switch (entry.Kind) {
                    case undoKind.RestoreAddressByIdAndCulture:
                        if (entry.HadValue) {
                            _addressByIdAndCulture[entry.Key] = entry.Address;
                        } else {
                            _addressByIdAndCulture.Remove(entry.Key);
                        }
                        break;
                    case undoKind.RestoreIdAndCultureByAddress:
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
            if (cultureCode.HasValue) {
                _cultureIdByCode.Remove(cultureCode.Value);
                _cultureCodeById[i] = null;
            }
        }

        _lastCultureId = _transactionStartCultureId;
        _undoLog?.Clear();
    }
    public bool TryGetId(string address, out int id, out Guid? cultureCode) {
        if (_idAndCultureByAddress.TryGetValue(address, out var key)) {
            id = unpackId(key);
            cultureCode = _cultureCodeById[unpackCultureId(key)];
            return true;
        }

        id = 0;
        cultureCode = null;
        return false;
    }
    public bool TryGetAddressAndTryMatchCulture(int id, Guid? cultureCode, [MaybeNullWhen(false)] out string? address) {
        if (!tryGetCultureId(cultureCode, out var cultureId)) {
            return TryGetFirstAddressAnyCulture(id, out address);
        }
        if (_addressByIdAndCulture.TryGetValue(packKey(id, cultureId), out var foundAddress)) {
            address = foundAddress;
            return true;
        }
        address = null;
        return false;
    }
    public bool TryGetFirstAddressAnyCulture(int id, [MaybeNullWhen(false)] out string? address) {
        for (int cultureId = 0; cultureId <= _lastCultureId; cultureId++) {
            if (_addressByIdAndCulture.TryGetValue(packKey(id, (byte)cultureId), out var foundAddress)) {
                address = foundAddress;
                return true;
            }
        }
        address = null;
        return false;
    }
    public void Update(int id, string? address, Guid? cultureCode, out string? newAddress, out bool changedNewAddress) {
        byte cultureId;
        if (address is null) {
            if (!tryGetCultureId(cultureCode, out cultureId)) {
                newAddress = null;
                changedNewAddress = false;
                return;
            }
        } else {
            cultureId = getOrAddCultureId(cultureCode);
        }
        var key = packKey(id, cultureId);
        _addressByIdAndCulture.TryGetValue(key, out var currentAddress);

        if (address is null) {
            if (currentAddress is not null) {
                removeAddressByIdAndCulture(key);
                removeIdAndCultureByAddress(currentAddress);
            }

            newAddress = null;
            changedNewAddress = false;
            return;
        }

        var candidate = address;
        if (_idAndCultureByAddress.TryGetValue(candidate, out var owner) && owner != key) {
            var suffix = 2;
            while (true) {
                candidate = createSuffixedAddress(address, suffix);
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
            removeIdAndCultureByAddress(currentAddress);
        }

        setAddressByIdAndCulture(key, candidate);
        setIdAndCultureByAddress(candidate, key);
        newAddress = candidate;
    }
    public void Remove(int id, string? address, Guid? cultureCode) {
        Update(id, null, cultureCode, out _, out _);
    }
    public void Remove(int id) {
        for (int cultureId = 0; cultureId <= _lastCultureId; cultureId++) {
            var key = packKey(id, (byte)cultureId);
            if (_addressByIdAndCulture.TryGetValue(key, out var address)) {
                removeAddressByIdAndCulture(key);
                removeIdAndCultureByAddress(address);
            }
        }
    }

    public void SaveState(IAppendStream stream) {
        stream.WriteMarker(_marker);
        stream.RecordChecksum();

        stream.WriteOneByte(_lastCultureId);
        stream.WriteVerifiedInt(_cultureIdByCode.Count);
        foreach (var kv in _cultureIdByCode) {
            stream.WriteGuid(kv.Key);
            stream.WriteOneByte(kv.Value);
        }

        stream.WriteVerifiedInt(_addressByIdAndCulture.Count);
        foreach (var kv in _addressByIdAndCulture) {
            stream.WriteLong(kv.Key);
            stream.WriteString(kv.Value);
        }

        stream.WriteChecksum();
        stream.WriteGuid(_marker);
    }
    public void ReadState(IReadStream stream) {
        stream.ValidateMarker(_marker);
        stream.RecordChecksum();

        _addressByIdAndCulture.Clear();
        _idAndCultureByAddress.Clear();
        _cultureIdByCode.Clear();
        Array.Clear(_cultureCodeById, 0, _cultureCodeById.Length);

        _lastCultureId = stream.ReadOneByte();
        var noCultures = stream.ReadVerifiedInt();
        for (var i = 0; i < noCultures; i++) {
            var cultureCode = stream.ReadGuid();
            var cultureId = stream.ReadOneByte();
            _cultureIdByCode[cultureCode] = cultureId;
            _cultureCodeById[cultureId] = cultureCode;
        }

        var noAddresses = stream.ReadVerifiedInt();
        for (var i = 0; i < noAddresses; i++) {
            var key = stream.ReadLong();
            var address = stream.ReadString();
            _addressByIdAndCulture[key] = address;
            _idAndCultureByAddress[address] = key;
        }

        stream.ValidateChecksum();
        stream.ValidateMarker(_marker);

        _inTransaction = false;
        _undoLog?.Clear();
        _transactionStartCultureId = _lastCultureId;
    }

}
