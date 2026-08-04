using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Relations;
using Relatude.DB.IO;
using Relatude.DB.Transactions;
using Relatude.DB.DataStores.Transactions;
namespace Relatude.DB.DataStores.Stores {
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
            var relation = _relations[ra.RelationId];
            if (ra.Operation == PrimitiveOperation.Add) {
                relation.Add(ra.Source, ra.Target, ra.ChangeUtc);
                // when this add is the opposite of a rolled back remove, put the edge back at its
                // original positions instead of leaving it appended at the end of the lists:
                if (ra.RestoreSourceListIndex >= 0) relation.Move(ra.Source, ra.Target, false, ra.RestoreSourceListIndex.Value);
                if (ra.RestoreTargetListIndex >= 0) relation.Move(ra.Target, ra.Source, true, ra.RestoreTargetListIndex.Value);
            } else {
                // capture positions before removing so a rollback (opposite add) can restore exact order:
                ra.RestoreSourceListIndex = relation.IndexOfRelated(ra.Source, ra.Target, false);
                ra.RestoreTargetListIndex = relation.IndexOfRelated(ra.Target, ra.Source, true);
                relation.Remove(ra.Source, ra.Target);
            }
        }
        public void RegisterAction(PrimitiveRelationReorderAction ra) {
            _relations[ra.RelationId].Move(ra.Owner, ra.Moved, ra.FromTargetToSource, ra.ToIndex);
        }
        public void RegisterActionIfPossible(PrimitiveRelationAction action) {
            if (action.Operation == PrimitiveOperation.Add) {
                _relations[action.RelationId].OnlyAddIfValid(action.Source, action.Target, action.ChangeUtc);
            } else {
                _relations[action.RelationId].OnlyRemoveIfValid(action.Source, action.Target);
            }
        }
        public void RegisterActionIfPossible(PrimitiveRelationReorderAction action) {
            _relations[action.RelationId].OnlyMoveIfValid(action.Owner, action.Moved, action.FromTargetToSource, action.ToIndex);
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
        internal void ReadState(BufferReader stream, Action<string?, int?> progress) {
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
        internal (Guid relId, RelData[] relations, PrimitiveRelationReorderAction[] reorders)[] Snapshot() {
            return _relations.Values.Select(r => {
                var edges = r.Values.ToArray();
                return (r.Id, edges, computeReplayReorders(r, edges));
            }).ToArray();
        }
        // Replaying a snapshot as a plain sequence of adds rebuilds every list in edge enumeration order,
        // which loses explicit reordering. Both sides of a many to many relation cannot even in theory
        // always be reproduced by add order alone (the ordering constraints can be cyclic), so the log
        // rewriter appends reorder actions for every list whose order would come back differently.
        static PrimitiveRelationReorderAction[] computeReplayReorders(Relation r, RelData[] edgesInReplayOrder) {
            List<PrimitiveRelationReorderAction> reorders = [];
            var symmetric = r.IsSymmetric;
            var replayedTargetsBySource = new Dictionary<int, List<int>>();
            var replayedSourcesByTarget = symmetric ? replayedTargetsBySource : new Dictionary<int, List<int>>(); // symmetric indexes keep a single list per participant
            static void append(Dictionary<int, List<int>> map, int key, int value) {
                if (map.TryGetValue(key, out var list)) list.Add(value);
                else map.Add(key, [value]);
            }
            foreach (var e in edgesInReplayOrder) {
                append(replayedTargetsBySource, e.Source, e.Target);
                if (!symmetric || e.Source != e.Target) append(replayedSourcesByTarget, e.Target, e.Source);
            }
            addReordersForDirection(r, replayedTargetsBySource, false, reorders);
            if (!symmetric) addReordersForDirection(r, replayedSourcesByTarget, true, reorders); // symmetric lists ignore direction, one pass covers all
            return [.. reorders];
        }
        static void addReordersForDirection(Relation r, Dictionary<int, List<int>> replayed, bool fromTargetToSource, List<PrimitiveRelationReorderAction> reorders) {
            foreach (var owner in r.DistinctIds(fromTargetToSource)) {
                var actual = r.GetRelated(owner, fromTargetToSource).Enumerate().ToList();
                if (actual.Count < 2) continue; // single valued lists always replay correctly
                if (!replayed.TryGetValue(owner, out var simulated)) continue;
                foreach (var (moved, fromIndex, toIndex) in RelationOrderUtils.DiffToMoves(simulated, actual)) {
                    reorders.Add(new PrimitiveRelationReorderAction(r.Id, owner, moved, fromTargetToSource, fromIndex, toIndex));
                }
            }
        }
    }
}
