using Relatude.DB.DataStores.StateStores;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Relatude.DB.DataStores.Stores;

/// <summary>
/// Registry of node addresses. The map holds id + culture -> address and mirrors the Address system
/// property of every node; the reverse lookup (address -> owners) comes from the map too. Several
/// nodes may own the same address (a url manager can produce unique complete URLs from non-unique
/// address segments), so the reverse lookup is multi-owner. Registration never changes the address
/// it is given - collision handling (the suffix loop) lives with the caller, which decides
/// uniqueness through the configured url manager.
/// Cultures are packed into the key as a byte; the culture table lives beside the map, in the state
/// file for the memory map and in the engine for an engine backed map.
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
    private readonly IAddressMap _map;
    private readonly Dictionary<Guid, byte> _cultureIdByCode = new();
    private readonly Guid?[] _cultureCodeById = new Guid?[256];
    private byte _lastCultureId = 0;
    private bool _inTransaction;
    private byte _transactionStartCultureId;
    private List<undoEntry>? _undoLog;

    readonly struct undoEntry(long key, string? address) {
        public readonly long Key = key;
        public readonly string? Address = address; // null: the key had no address
    }

    public AddressRegistry(IAddressMap map) {
        _map = map;
        if (_map.PersistedByEngine) readCultures(_map.Meta);
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
        if (_map.PersistedByEngine) _map.Meta = writeCultures();
        return cultureId;
    }
    void readCultures(string? meta) {
        _cultureIdByCode.Clear();
        Array.Clear(_cultureCodeById);
        _lastCultureId = 0;
        if (string.IsNullOrEmpty(meta)) return;
        var parts = meta.Split('|');
        _lastCultureId = byte.Parse(parts[0]);
        for (var i = 1; i < parts.Length; i++) {
            var pair = parts[i].Split(':');
            var code = Guid.Parse(pair[0]);
            var id = byte.Parse(pair[1]);
            _cultureIdByCode[code] = id;
            _cultureCodeById[id] = code;
        }
    }
    string writeCultures() => _lastCultureId + string.Concat(_cultureIdByCode.Select(kv => "|" + kv.Key + ":" + kv.Value));

    void logUndo(long key) {
        if (!_inTransaction || _undoLog is null) return;
        _undoLog.Add(new undoEntry(key, _map.TryGet(key, out var existing) ? existing : null));
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
                if (entry.Address is not null) _map.Set(entry.Key, entry.Address);
                else _map.Remove(entry.Key);
            }
        }
        for (int i = _lastCultureId; i > _transactionStartCultureId; i--) {
            var cultureCode = _cultureCodeById[i];
            if (cultureCode.HasValue) {
                _cultureIdByCode.Remove(cultureCode.Value);
                _cultureCodeById[i] = null;
            }
        }
        if (_lastCultureId != _transactionStartCultureId) {
            _lastCultureId = _transactionStartCultureId;
            if (_map.PersistedByEngine) _map.Meta = writeCultures();
        }
        _undoLog?.Clear();
    }
    /// <summary>First owner of the address, for callers that expect the single-owner behavior.</summary>
    public bool TryGetId(string address, out int id, out Guid? cultureCode) {
        var owners = _map.GetOwners(address);
        if (owners.Length > 0) {
            id = unpackId(owners[0]);
            cultureCode = _cultureCodeById[unpackCultureId(owners[0])];
            return true;
        }
        id = 0;
        cultureCode = null;
        return false;
    }
    /// <summary>Every owner of the address.</summary>
    public (int id, Guid? cultureCode)[] GetOwners(string address) {
        var owners = _map.GetOwners(address);
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
        if (_map.TryGet(packKey(id, cultureId), out var foundAddress)) {
            address = foundAddress;
            return true;
        }
        address = null;
        return false;
    }
    public bool TryGetFirstAddressAnyCulture(int id, [MaybeNullWhen(false)] out string? address) {
        for (int cultureId = 0; cultureId <= _lastCultureId; cultureId++) {
            if (_map.TryGet(packKey(id, (byte)cultureId), out var foundAddress)) {
                address = foundAddress;
                return true;
            }
        }
        address = null;
        return false;
    }
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
        var currentAddress = _map.TryGet(key, out var found) ? found : null;
        if (address is null) {
            if (currentAddress is not null) {
                logUndo(key);
                _map.Remove(key);
            }
            return;
        }
        if (string.Equals(currentAddress, address, StringComparison.Ordinal)) {
            return; // unchanged
        }
        logUndo(key);
        _map.Set(key, address);
    }
    public void Remove(int id, Guid? cultureCode) {
        Register(id, null, cultureCode);
    }
    public void Remove(int id) {
        for (int cultureId = 0; cultureId <= _lastCultureId; cultureId++) {
            var key = packKey(id, (byte)cultureId);
            if (_map.TryGet(key, out _)) {
                logUndo(key);
                _map.Remove(key);
            }
        }
    }

    // an engine backed map writes -1 instead of the address count, so a state file written by the other kind of store is detected
    public void SaveState(IAppendStream stream) {
        stream.WriteMarker(_marker);
        stream.RecordChecksum();
        if (_map.PersistedByEngine) {
            stream.WriteOneByte(0);
            stream.WriteVerifiedInt(0);
            stream.WriteVerifiedInt(-1);
        } else {
            stream.WriteOneByte(_lastCultureId);
            stream.WriteVerifiedInt(_cultureIdByCode.Count);
            foreach (var kv in _cultureIdByCode) {
                stream.WriteGuid(kv.Key);
                stream.WriteOneByte(kv.Value);
            }
            stream.WriteVerifiedInt(_map.Count);
            foreach (var kv in _map.Entries) {
                stream.WriteLong(kv.Key);
                stream.WriteString(kv.Value);
            }
        }
        stream.WriteChecksum();
        stream.WriteGuid(_marker);
    }
    public void ReadState(BufferReader stream) {
        stream.ValidateMarker(_marker);
        stream.RecordChecksum();
        var lastCultureId = stream.ReadOneByte();
        var noCultures = stream.ReadVerifiedInt();
        var cultures = new (Guid code, byte id)[noCultures];
        for (var i = 0; i < noCultures; i++) cultures[i] = (stream.ReadGuid(), stream.ReadOneByte());
        var noAddresses = stream.ReadVerifiedInt();
        if (_map.PersistedByEngine != (noAddresses < 0)) throw new Exception("The state file was written by another kind of state store. ");
        if (!_map.PersistedByEngine) {
            _cultureIdByCode.Clear();
            Array.Clear(_cultureCodeById, 0, _cultureCodeById.Length);
            _lastCultureId = lastCultureId;
            foreach (var (code, id) in cultures) {
                _cultureIdByCode[code] = id;
                _cultureCodeById[id] = code;
            }
            for (var i = 0; i < noAddresses; i++) {
                var key = stream.ReadLong();
                var address = stream.ReadString();
                _map.Set(key, address);
            }
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
