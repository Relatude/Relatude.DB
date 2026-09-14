using Relatude.DB.Common;
using Relatude.DB.DataStores.StateStores;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Relatude.DB.DataStores.Stores {
    internal struct IdPair(int id, Guid guid) {
        public readonly int Id = id;
        public readonly Guid Guid = guid;

    }
    internal class GuidStore : IDisposable {
        readonly IGuidMap _map;
        public GuidStore(IGuidMap map) { _map = map; }
        object _lock = new object();
        List<IdPair>? _newIds = null;
        int _lastIdOnStartOfRecording;
        int newId() {
            // will look for first available id, starting by incrementing from last generated
            var lastId = _map.LastId;
            if (lastId == int.MaxValue) lastId = 0; // start over
            while (true) {
                lastId++;
                if (!_map.TryGetGuid(lastId, out _)) {
                    _map.LastId = lastId;
                    return lastId;
                }
                if (lastId == int.MaxValue) throw new Exception("Ran out of unique 32 bit ids. Too much data. ");
            }
        }
        public void BeginTransaction() {
            lock (_lock) {
                if (_newIds != null) throw new("Recording started before last was completed. ");
                _newIds = new List<IdPair>();
                _lastIdOnStartOfRecording = _map.LastId;
            }
        }
        public void Commit() {
            lock (_lock) {
                _newIds = null;
            }
        }
        public void RollbackIfUncommited() {
            lock (_lock) {
                if (_newIds == null) return;
                foreach (var pair in _newIds) _map.Remove(pair.Id, pair.Guid);
                _map.LastId = _lastIdOnStartOfRecording;
                _newIds = null;
            }
        }
        public void Add(int id, Guid guid) {
            lock (_lock) _map.Add(id, guid);
        }
        public void Remove(int id, Guid guid) {
            lock (_lock) _map.Remove(id, guid);
        }
        public void ValidateExistence(int id, Guid guid) {
            lock (_lock) {
                if (!_map.TryGetId(guid, out var id2)) throw new Exception("Guid not found. ");
                if (id2 != id) throw new Exception("Guid is associated with different id. ");
                if (!_map.TryGetGuid(id, out var guid2)) throw new Exception("Id not found. ");
                if (guid2 != guid) throw new Exception("Id is associated with different guid. ");
            }
        }
        public void ValidateCombinationOfIdAndGuid(int id, Guid guid) {
            lock (_lock) {
                if (_map.TryGetId(guid, out var id2)) {
                    if (id2 != id) throw new Exception("Suggested guid is already associated with different id. ");
                }
                if (_map.TryGetGuid(id, out var guid2)) {
                    if (guid2 != guid) throw new Exception("Suggested id is already associated with different guid. ");
                }
            }
        }
        public void ValidateCombinationAndRegisterIfNew(int id, Guid guid) {
            lock (_lock) {
                bool foundId = false;
                bool foundGuid = false;
                if (_map.TryGetId(guid, out var id2)) {
                    if (id2 != id) throw new Exception("Suggested guid is already associated with different id. ");
                    foundId = true;
                }
                if (_map.TryGetGuid(id, out var guid2)) {
                    if (guid2 != guid) throw new Exception("Suggested id is already associated with different guid. ");
                    foundGuid = true;
                }
                if (foundId && foundGuid) return;
                if (foundId != foundGuid) throw new Exception("Inconsistent ID state. ");  // should never happen..
                _map.Add(id, guid);
            }
        }
        public void RegisterAction(PrimitiveActionBase action) {
            lock (_lock) {
                if (action is PrimitiveNodeAction na) {
                    ValidateCombinationOfIdAndGuid(na.Node.__Id, na.Node.Id);
                    switch (na.Operation) {
                        case PrimitiveOperation.Add: _map.Add(na.Node.__Id, na.Node.Id); break;
                        case PrimitiveOperation.Remove: _map.Remove(na.Node.__Id, na.Node.Id); break;
                        default: throw new NotImplementedException();
                    }
                    if (na.Node.__Id > _map.LastId) _map.LastId = na.Node.__Id;
                }
            }
        }
        public Guid GetGuid(int id) {
            lock (_lock) {
                if (!_map.TryGetGuid(id, out var guid)) {
                    throw new InvalidOperationException("Unknown id: " + id + ". ");
                }
                return guid;
            }
        }
        public Guid GetGuidOrCreate(int id) {
            if (id == 0) throw new InvalidOperationException("Unable to create guid for empty id. ");
            lock (_lock) {
                if (!_map.TryGetGuid(id, out var guid)) {
                    guid = Guid.NewGuid();
                    _map.Add(id, guid);
                    if (_newIds == null) throw new Exception("Unable to record new ids. ");
                    _newIds.Add(new IdPair(id, guid));
                }
                return guid;
            }
        }
        public int ValidateAndReturnIntId(NodeKey key) {
            lock (_lock) {
                if (key.HasInt && key.HasGuid) {
                    ValidateCombinationOfIdAndGuid(key.Int, key.Guid);
                    return key.Int;
                }
                if (key.HasInt) {
                    if (!_map.TryGetGuid(key.Int, out _)) throw new InvalidOperationException("Unknown id: " + key.Int + ". ");
                    return key.Int;
                }
                if (key.HasGuid) {
                    if (!_map.TryGetId(key.Guid, out var id)) throw new InvalidOperationException("Unknown guid: " + key.Guid + ". ");
                    return id;
                }
                throw new InvalidOperationException("Unable to validate id key. ");
            }
        }
        public int GetId(Guid guid) {
            lock (_lock) {
                if (!_map.TryGetId(guid, out var id)) {
                    throw new InvalidOperationException("Unknown node: " + guid + ". ");
                }
                return id;
            }
        }
        public bool TryGetId(Guid guid, out int id) {
            lock (_lock) {
                return _map.TryGetId(guid, out id);
            }
        }
        public bool TryGetId(int id, out Guid guid) {
            lock (_lock) {
                return _map.TryGetGuid(id, out guid);
            }
        }
        public int GetIdOrCreate(Guid guid) {
            lock (_lock) {
                if (guid == Guid.Empty) throw new InvalidOperationException("Unable to create id for empty guid. ");
                if (!_map.TryGetId(guid, out var id)) {
                    id = newId();
                    _map.Add(id, guid);
                    if (_newIds == null) throw new Exception("Unable to record new ids. ");
                    _newIds.Add(new IdPair(id, guid));
                }
                return id;
            }
        }
        public void Dispose() {
        }
        static Guid _marker = new Guid("510a2795-352d-4054-abcf-7e5a0ce0136b");
        // an engine backed map writes -1 instead of a count, so a state file written by the other kind of store is detected
        public void SaveState(IAppendStream stream) {
            stream.WriteMarker(_marker);
            stream.RecordChecksum();
            var count = _map.PersistedByEngine ? -1 : _map.Count;
            stream.WriteVerifiedInt(count);
            if (count > 0) {
                foreach (var kv in _map.Entries) {
                    stream.WriteUInt((uint)kv.Key);
                    stream.WriteGuid(kv.Value);
                }
            }
            stream.WriteChecksum();
            stream.WriteGuid(_marker);
        }
        public void ReadState(BufferReader stream) {
            stream.ValidateMarker(_marker);
            stream.RecordChecksum();
            var noIds = stream.ReadVerifiedInt();
            if (_map.PersistedByEngine != (noIds < 0)) throw new Exception("The state file was written by another kind of state store. ");
            if (noIds > 0) {
                _map.EnsureCapacity(noIds);
                var lastId = _map.LastId;
                for (int i = 0; i < noIds; i++) {
                    var id = (int)stream.ReadUInt();
                    _map.Add(id, stream.ReadGuid());
                    if (id > lastId) lastId = id;
                }
                _map.LastId = lastId;
            }
            stream.ValidateChecksum();
            stream.ValidateMarker(_marker);
        }
    }
}
