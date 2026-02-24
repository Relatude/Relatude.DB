using System;
using Relatude.DB.Common;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
using Relatude.DB.DataStores.Transactions;

namespace Relatude.DB.DataStores.Stores {
    internal struct IdPair {
        public IdPair(int id, Guid guid) {
            Id = id;
            Guid = guid;
        }
        public readonly int Id;
        public readonly Guid Guid;
    }
    internal class GuidStore : IDisposable {
        object _lock = new object();
        readonly Dictionary<Guid, int> _ids;
        readonly Dictionary<int, Guid> _guids;
        int _lastId;
        List<IdPair>? _newIds = null;
        int _lastIdOnStartOfRecording;
        public GuidStore() {
            _ids = new Dictionary<Guid, int>();
            _guids = new Dictionary<int, Guid>();
            _lastId = 0;
        }
        int newId() {
            // will look for first available id, starting by incrementing from last generated
            if (_lastId == int.MaxValue) _lastId = 0; // start over
            while (true) {
                _lastId++;
                if (!_guids.ContainsKey(_lastId)) return _lastId;
                if (_lastId == int.MaxValue) throw new Exception("Ran out of unique 32 bit ids. Too much data. ");
            }
        }
        public void StartRecordingNewIds() {
            lock (_lock) {
                if (_newIds != null) throw new("Recording started before last was completed. ");
                _newIds = new List<IdPair>();
                _lastIdOnStartOfRecording = _lastId;
            }
        }
        public void CommitNewIds() {
            lock (_lock) {
                _newIds = null;
            }
        }
        public void CancelUnCommitedNewIdsIfAny() {
            lock (_lock) {
                if (_newIds == null) return;
                foreach (var pair in _newIds) {
                    _guids.Remove(pair.Id);
                    _ids.Remove(pair.Guid);
                }
                _lastId = _lastIdOnStartOfRecording;
                _newIds = null;
            }
        }
        public void Add(int id, Guid guid) {
            lock (_lock) {
                _ids.Add(guid, id);
                _guids.Add(id, guid);
            }
        }
        public void Remove(int id, Guid guid) {
            lock (_lock) {
                _ids.Remove(guid);
                _guids.Remove(id);
            }
        }
        public void ValidateExistence(int id, Guid guid) {
            lock (_lock) {
                if (!_ids.TryGetValue(guid, out var id2)) throw new Exception("Guid not found. ");
                if (id2 != id) throw new Exception("Guid is associated with different id. ");
                if (!_guids.TryGetValue(id, out var guid2)) throw new Exception("Id not found. ");
                if (guid2 != guid) throw new Exception("Id is associated with different guid. ");
            }
        }
        public void ValidateCombinationOfIdAndGuid(int id, Guid guid) {
            lock (_lock) {
                if (_ids.TryGetValue(guid, out var id2)) {
                    if (id2 != id) throw new Exception("Suggested guid is already associated with different id. ");
                }
                if (_guids.TryGetValue(id, out var guid2)) {
                    if (guid2 != guid) throw new Exception("Suggested id is already associated with different guid. ");
                }
            }
        }
        public void ValidateCombinationAndRegisterIfNew(int id, Guid guid) {
            lock (_lock) {
                bool foundId = false;
                bool foundGuid = false;
                if (_ids.TryGetValue(guid, out var id2)) {
                    if (id2 != id) throw new Exception("Suggested guid is already associated with different id. ");
                    foundId = true;
                }
                if (_guids.TryGetValue(id, out var guid2)) {
                    if (guid2 != guid) throw new Exception("Suggested id is already associated with different guid. ");
                    foundGuid = true;
                }
                if (foundId && foundGuid) return;
                if (foundId != foundGuid) throw new Exception("Inconsistent ID state. ");  // should never happen..
                _ids.Add(guid, id);
                _guids.Add(id, guid);
            }
        }
        public void ChangeGuid(Guid oldGuid, Guid newGuid) {
            lock (_lock) {
                var id = _ids[oldGuid];
                if (_ids.ContainsKey(newGuid)) throw new Exception("New guid is already in use. ");
                _guids[id] = newGuid;
                _ids.Remove(oldGuid);
                _ids.Add(newGuid, id);
            }
        }
        public void RegisterAction(PrimitiveActionBase action) {
            lock (_lock) {
                if (action is PrimitiveNodeAction na) {
                    ValidateCombinationOfIdAndGuid(na.Node.__Id, na.Node.Id);
                    switch (na.Operation) {
                        case PrimitiveOperation.Add: Add(na.Node.__Id, na.Node.Id); break;
                        case PrimitiveOperation.Remove: Remove(na.Node.__Id, na.Node.Id); break;
                        default: throw new NotImplementedException();
                    }
                    if (na.Node.__Id > _lastId) _lastId = na.Node.__Id;
                }
            }
        }
        public Guid GetGuid(int id) {
            lock (_lock) {
                if (!_guids.TryGetValue(id, out var guid)) {
                    throw new InvalidOperationException("Unknown id: " + id + ". ");
                }
                return guid;
            }
        }
        public Guid GetGuidOrCreate(int id) {
            if (id == 0) throw new InvalidOperationException("Unable to create guid for empty id. ");
            lock (_lock) {
                if (!_guids.TryGetValue(id, out var guid)) {
                    guid = Guid.NewGuid();
                    _ids.Add(guid, id);
                    _guids.Add(id, guid);
                    if (_newIds == null) throw new Exception("Unable to record new ids. ");
                    _newIds.Add(new IdPair(id, guid));
                }
                return guid;
            }
        }
        public int ValidateAndReturnIntId(IdKey key) {
            lock (_lock) {
                if (key.HasInt && key.HasInt) {
                    ValidateCombinationOfIdAndGuid(key.Int, key.Guid);
                    return key.Int;
                }
                if (key.HasInt) {
                    if (!_guids.ContainsKey(key.Int)) throw new InvalidOperationException("Unknown id: " + key.Int + ". ");
                    return key.Int;
                }
                if (key.HasGuid) {
                    if (!_ids.TryGetValue(key.Guid, out var id)) throw new InvalidOperationException("Unknown guid: " + key.Guid + ". ");
                    return id;
                }
                throw new InvalidOperationException("Unable to validate id key. ");
            }
        }
        public int GetId(Guid guid) {
            lock (_lock) {
                if (!_ids.TryGetValue(guid, out var id)) {
                    throw new InvalidOperationException("Unknown node: " + guid + ". ");
                }
                return id;
            }
        }
        public bool TryGetId(Guid guid, out int id) {
            lock (_lock) {
                return _ids.TryGetValue(guid, out id);
            }
        }
        public bool TryGetId(int id, out Guid guid) {
            lock (_lock) {
                return _guids.TryGetValue(id, out guid);
            }
        }
        public int GetIdOrCreate(Guid guid) {
            lock (_lock) {
                if (guid == Guid.Empty) throw new InvalidOperationException("Unable to create id for empty guid. ");
                if (!_ids.TryGetValue(guid, out var id)) {
                    id = newId();
                    _guids.Add(id, guid);
                    _ids.Add(guid, id);
                    if (_newIds == null) throw new Exception("Unable to record new ids. ");
                    _newIds.Add(new IdPair(id, guid));
                }
                return id;
            }
        }
        public void Dispose() {
        }
        static Guid _marker = new Guid("510a2795-352d-4054-abcf-7e5a0ce0136b");
        public void SaveState(IAppendStream stream) {
            stream.WriteMarker(_marker);
            stream.RecordChecksum();
            stream.WriteVerifiedInt(_ids.Count);
            foreach (var kv in _ids) {
                stream.WriteUInt((uint)kv.Value);
                stream.WriteGuid(kv.Key);
            }
            stream.WriteChecksum();
            stream.WriteGuid(_marker);
        }
        public void ReadState(IReadStream stream) {
            stream.ValidateMarker(_marker);
            stream.RecordChecksum();
            var noIds = stream.ReadVerifiedInt();
            for (int i = 0; i < noIds; i++) {
                Add((int)stream.ReadUInt(), stream.ReadGuid());
            }
            stream.ValidateChecksum();
            stream.ValidateMarker(_marker);
        }
        internal string? TextInfo() {
            lock (_lock) {
                return "ID count: " + _ids.Count.To1000N() + "\n";
            }
        }

        internal int GetId(IdKey nodeIdKey) {
            throw new NotImplementedException();
        }
    }
}
