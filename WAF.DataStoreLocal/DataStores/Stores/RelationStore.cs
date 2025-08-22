using WAF.Datamodels;
using WAF.DataStores.Definitions;
using WAF.DataStores.Relations;
using WAF.IO;
using WAF.Transactions;
using WAF.DataStores.Transactions;
namespace WAF.DataStores.Stores {
    internal class RelationStore {
        readonly Definition _definition;
        readonly Dictionary<Guid, Relation> _relations;
        public RelationStore(Definition definition) {
            _definition = definition;
            _relations = new Dictionary<Guid, Relation>();
            foreach (var r in _definition.Relations.Values) {
                _relations.Add(r.Id, r);
            }
        }
        public void CompressMemory() {
            foreach (var r in _relations.Values) {
                r.CompressMemory();
            }
        }
        public void RegisterAction(PrimitiveRelationAction ra) {
            if (ra.Operation == PrimitiveOperation.Add) {
                _relations[ra.RelationId].Add(ra.Source, ra.Target, ra.ChangeUtc);
            } else {
                _relations[ra.RelationId].Remove(ra.Source, ra.Target);
            }
        }
        public void RegisterActionIfPossible(PrimitiveRelationAction action) {
            if (action.Operation == PrimitiveOperation.Add) {
                _relations[action.RelationId].OnlyAddIfValid(action.Source, action.Target, action.ChangeUtc);
            } else {
                _relations[action.RelationId].OnlyRemoveIfValid(action.Source, action.Target);
            }
        }
        static Guid _marker = new Guid("5ce5c596-4c62-47a6-9940-7a6c7a760d14");
        static Guid _markerNewRel = new Guid("6242ecc3-4ea2-4542-a5d7-b8ecc3b9bd9a");
        internal int TotalCount() => _relations.Values.Sum(r => r.Count);
        internal void SaveState(IAppendStream stream) {
            stream.WriteGuid(_marker);
            stream.RecordChecksum();
            stream.WriteVerifiedInt(_relations.Count);
            foreach (var relation in _relations.Values) {
                stream.WriteMarker(_markerNewRel);
                stream.WriteGuid(relation.Id);
                relation.SaveState(stream);
            }
            stream.WriteChecksum();
            stream.WriteGuid(_marker);
        }
        internal void ReadState(IReadStream stream, Action<string?, int?> progress) {
            stream.ValidateMarker(_marker);
            stream.RecordChecksum();
            var noRelations = stream.ReadVerifiedInt();
            for (var i = 0; i < noRelations; i++) {
                progress("Reading relation " + (i + 1) + " of " + noRelations, (i * 100 / noRelations));
                stream.ValidateMarker(_markerNewRel);
                var id = stream.ReadGuid();
                if (_relations.TryGetValue(id, out var relation)) {
                    relation.ReadState(stream);
                } else {
                    throw new InvalidDataException();
                }
            }
            stream.ValidateChecksum();
            stream.ValidateMarker(_marker);
        }
        internal (Guid relId, RelData[])[] Snapshot() {
            return _relations.Values.Select(r => (r.Id, r.Values.ToArray())).ToArray();
        }
    }
}
