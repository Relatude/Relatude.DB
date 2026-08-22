using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Relatude.DB.DataStores.Stores;

/// <summary>
/// In-memory registry of node addresses. The forward map (id + culture -> address) mirrors the
/// Address system property of every node; the reverse map (address -> owners) supports lookups.
/// Several nodes may own the same address (a url manager can produce unique complete URLs from
/// non-unique address segments), so the reverse map is multi-owner. Registration never changes
/// the address it is given - collision handling (the suffix loop) lives with the caller, which
/// decides uniqueness through the configured url manager.
/// The persisted state only contains the forward map, so the multi-owner reverse map is a pure
/// in-memory rebuild and the state file format is unchanged from the single-owner registry.
/// </summary>
public class AddressRegistry {
    private static readonly Guid _marker = new("fa5f4dd3-8520-4fc9-a260-637fe9ddb2ca");
    private static readonly byte[] _normalizeTable = BuildNormalizeTable();
    private static byte[] BuildNormalizeTable() {
        var t = new byte[128];
        for (int i = 'a'; i <= 'z'; i++) t[i] = (byte)i;
        for (int i = 'A'; i <= 'Z'; i++) t[i] = (byte)(i + 32);
        for (int i = '0'; i <= '9'; i++) t[i] = (byte)i;
        t['-'] = (byte)'-'; t['/'] = (byte)'/'; t['_'] = (byte)'_';
        return t;
    }
    private readonly Dictionary<long, string> _addressByIdAndCulture = new();
    // owner arrays are treated as immutable and replaced on change, so undo entries can hold the previous array by reference
    private readonly Dictionary<string, long[]> _ownersByAddress = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, byte> _cultureIdByCode = new();
    private readonly Guid?[] _cultureCodeById = new Guid?[256];
    private byte _lastCultureId = 0;
    private bool _inTransaction;
    private byte _transactionStartCultureId;
    private List<undoEntry>? _undoLog;

    enum undoKind : byte {
        RestoreAddressByIdAndCulture,
        RestoreOwnersByAddress,
    }

    readonly struct undoEntry {
        public readonly undoKind Kind;
        public readonly long Key;
        public readonly string Address;
        public readonly long[]? Owners;
        public readonly bool HadValue;

        public undoEntry(undoKind kind, long key, string address, long[]? owners, bool hadValue) {
            Kind = kind;
            Key = key;
            Address = address;
            Owners = owners;
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
                _undoLog.Add(new undoEntry(undoKind.RestoreAddressByIdAndCulture, key, existing, null, true));
            } else {
                _undoLog.Add(new undoEntry(undoKind.RestoreAddressByIdAndCulture, key, string.Empty, null, false));
            }
        }

        _addressByIdAndCulture[key] = value;
    }

    void removeAddressByIdAndCulture(long key) {
        if (!_addressByIdAndCulture.TryGetValue(key, out var existing)) {
            return;
        }

        if (_inTransaction && _undoLog is not null) {
            _undoLog.Add(new undoEntry(undoKind.RestoreAddressByIdAndCulture, key, existing, null, true));
        }

        _addressByIdAndCulture.Remove(key);
    }

    void logOwnersUndo(string address) {
        if (!_inTransaction || _undoLog is null) return;
        if (_ownersByAddress.TryGetValue(address, out var existing)) {
            _undoLog.Add(new undoEntry(undoKind.RestoreOwnersByAddress, default, address, existing, true));
        } else {
            _undoLog.Add(new undoEntry(undoKind.RestoreOwnersByAddress, default, address, null, false));
        }
    }

    void addOwner(string address, long key) {
        logOwnersUndo(address);
        if (_ownersByAddress.TryGetValue(address, out var owners)) {
            foreach (var o in owners) if (o == key) return; // already registered
            var newOwners = new long[owners.Length + 1];
            Array.Copy(owners, newOwners, owners.Length);
            newOwners[^1] = key;
            _ownersByAddress[address] = newOwners;
        } else {
            _ownersByAddress[address] = [key];
        }
    }

    void removeOwner(string address, long key) {
        if (!_ownersByAddress.TryGetValue(address, out var owners)) return;
        var index = Array.IndexOf(owners, key);
        if (index == -1) return;
        logOwnersUndo(address);
        if (owners.Length == 1) {
            _ownersByAddress.Remove(address);
        } else {
            var newOwners = new long[owners.Length - 1];
            Array.Copy(owners, newOwners, index);
            Array.Copy(owners, index + 1, newOwners, index, owners.Length - index - 1);
            _ownersByAddress[address] = newOwners;
        }
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
                    case undoKind.RestoreOwnersByAddress:
                        if (entry.HadValue) {
                            _ownersByAddress[entry.Address] = entry.Owners!;
                        } else {
                            _ownersByAddress.Remove(entry.Address);
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
    /// <summary>First owner of the address, for callers that expect the single-owner behavior.</summary>
    public bool TryGetId(string address, out int id, out Guid? cultureCode) {
        if (_ownersByAddress.TryGetValue(address, out var owners) && owners.Length > 0) {
            id = unpackId(owners[0]);
            cultureCode = _cultureCodeById[unpackCultureId(owners[0])];
            return true;
        }

        id = 0;
        cultureCode = null;
        return false;
    }
    /// <summary>Every owner of the address, in registration order.</summary>
    public (int id, Guid? cultureCode)[] GetOwners(string address) {
        if (!_ownersByAddress.TryGetValue(address, out var owners)) return [];
        var result = new (int, Guid?)[owners.Length];
        for (int i = 0; i < owners.Length; i++) {
            result[i] = (unpackId(owners[i]), _cultureCodeById[unpackCultureId(owners[i])]);
        }
        return result;
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
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public string? NormalizeAddress(string? address, out bool changed) {
        if (string.IsNullOrEmpty(address)) { changed = false; return address; }
        var table = _normalizeTable;
        int outLen = 0;
        bool needsChange = false;
        for (int i = 0; i < address.Length; i++) {
            char c = address[i];
            byte mapped = c < 128 ? table[c] : (byte)0;
            if (mapped != 0) { outLen++; if (mapped != c) needsChange = true; } else needsChange = true;
        }
        if (!needsChange) { changed = false; return address; }
        changed = true;
        if (outLen == 0) return string.Empty;
        return string.Create(outLen, address, static (span, src) => {
            var t = _normalizeTable;
            int j = 0;
            for (int i = 0; i < src.Length; i++) {
                char c = src[i];
                byte b = c < 128 ? t[c] : (byte)0;
                if (b != 0) span[j++] = (char)b;
            }
        });
    }
    /// <summary>
    /// Registers the (already normalized) address of a node for a culture, replacing any previous
    /// address of that node and culture. A null address removes the registration. The address is
    /// stored exactly as given - uniqueness is the caller's decision.
    /// </summary>
    public void Register(int id, string? address, Guid? cultureCode) {
        byte cultureId;
        if (address == null) {
            if (!tryGetCultureId(cultureCode, out cultureId)) {
                return; // unknown culture, nothing can be registered for it
            }
        } else {
            cultureId = getOrAddCultureId(cultureCode);
        }
        var key = packKey(id, cultureId);
        _addressByIdAndCulture.TryGetValue(key, out var currentAddress);

        if (address is null) {
            if (currentAddress is not null) {
                removeAddressByIdAndCulture(key);
                removeOwner(currentAddress, key);
            }
            return;
        }

        if (string.Equals(currentAddress, address, StringComparison.Ordinal)) {
            return; // unchanged
        }

        if (currentAddress is not null) {
            removeOwner(currentAddress, key);
        }

        setAddressByIdAndCulture(key, address);
        addOwner(address, key);
    }
    public void Remove(int id, Guid? cultureCode) {
        Register(id, null, cultureCode);
    }
    public void Remove(int id) {
        for (int cultureId = 0; cultureId <= _lastCultureId; cultureId++) {
            var key = packKey(id, (byte)cultureId);
            if (_addressByIdAndCulture.TryGetValue(key, out var address)) {
                removeAddressByIdAndCulture(key);
                removeOwner(address, key);
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
    public void ReadState(BufferReader stream) {
        stream.ValidateMarker(_marker);
        stream.RecordChecksum();

        _addressByIdAndCulture.Clear();
        _ownersByAddress.Clear();
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

        _inTransaction = false; // no undo logging while rebuilding the reverse map below
        var noAddresses = stream.ReadVerifiedInt();
        for (var i = 0; i < noAddresses; i++) {
            var key = stream.ReadLong();
            var address = stream.ReadString();
            _addressByIdAndCulture[key] = address;
            addOwner(address, key);
        }

        stream.ValidateChecksum();
        stream.ValidateMarker(_marker);

        _inTransaction = false;
        _undoLog?.Clear();
        _transactionStartCultureId = _lastCultureId;
    }

    internal void RegisterActionDuringStateLoad(PrimitiveNodeAction na, bool throwOnErrors, Action<string, Exception?> logError) {
        try {
            switch (na.Operation) {
                case PrimitiveOperation.Add:
                    // the stored action already contains the final address, so it is registered verbatim
                    Register(na.Node.__Id, NormalizeAddress(na.Node.Address, out _), na.Node.Meta?.CultureId);
                    break;
                case PrimitiveOperation.Remove:
                    Remove(na.Node.__Id, na.Node.Meta?.CultureId);
                    break;
                default:
                    break;
            }
        } catch (Exception e) {
            var message = $"Error processing action {na} during state load: {e.Message}";
            logError?.Invoke(message, e);
            if (throwOnErrors) throw new InvalidOperationException(message, e);
        }
    }
}
