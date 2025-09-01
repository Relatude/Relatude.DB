using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Relations;
using Relatude.DB.IO;
using Relatude.DB.DataStores.Sets;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.DataStores.Definitions {
    internal class Relation {
        IRelationIndex _index = null!; // Always initialized in Initialize
        DataStoreLocal _store;
        public Relation(RelationModel rm, DataStoreLocal store) {
            Model = rm;
            _store = store;
            switch (RelationType) {
                case RelationType.OneOne: MaxCountFrom = MaxCountTo = 1; break;
                case RelationType.OneToOne: MaxCountFrom = MaxCountTo = 1; break;
                case RelationType.OneToMany: MaxCountFrom = 1; MaxCountTo = rm.MaxCountTo; break;
                case RelationType.ManyMany: MaxCountFrom = MaxCountTo = Math.Min(rm.MaxCountFrom, rm.MaxCountTo); break;
                case RelationType.ManyToMany: MaxCountFrom = rm.MaxCountFrom; MaxCountTo = rm.MaxCountTo; break;
                default: break;
            }
        }
        public bool IsSymmetric { get => _index.IsSymmetric; }
        object _stateLock = new();
        long? _stateId = null;
        void newState() {
            lock (_stateLock) {
                if (_stateId.HasValue) _stateId = null;
            }
        }
        public long GeneralStateId {
            get {
                lock (_stateLock) {
                    if (!_stateId.HasValue) _stateId = SetRegister.NewStateId();
                }
                return _stateId.Value;
            }
        }
        public readonly RelationModel Model;
        public Guid Id { get => Model.Id; }
        public HashSet<Guid> AllSourceTypes = null!; // Always initialized in Initialize
        public HashSet<Guid> AllTargetTypes = null!; // Always initialized in Initialize
        public RelationType RelationType { get => Model.RelationType; }
        public void Initialize(Definition def) {
            switch (RelationType) {
                case RelationType.OneOne: _index = new OneOneIndex(def.Sets); break;
                case RelationType.OneToOne: _index = new OneToOneIndex(def.Sets); break;
                case RelationType.OneToMany: _index = new OneToManyIndex(def.Sets); break;
                case RelationType.ManyMany: _index = new ManyManyIndex(); break;
                case RelationType.ManyToMany: _index = new ManyToManyIndex(); break;
                default: break;
            }
            var types = _store._definition.Datamodel.NodeTypes;
            AllSourceTypes = Model.SourceTypes.Select(t => types[t].ThisAndDescendingTypes.Keys).SelectMany(t => t).ToHashSet();
            AllTargetTypes = Model.TargetTypes.Select(t => types[t].ThisAndDescendingTypes.Keys).SelectMany(t => t).ToHashSet();
        }
        public void Add(int source, int target, DateTime dtUtc) {
            if (!canAdd(source, target, out var reason)) throw new ExceptionWithoutIntegrityLoss(reason);
            newState();
            _index.Add(source, target, dtUtc);
        }
        public void Remove(int source, int target) {
            if (!canRemove(source, target, out var reason)) throw new ExceptionWithoutIntegrityLoss(reason);
            newState();
            _index.Remove(source, target);
        }
        public void OnlyAddIfValid(int source, int target, DateTime dtUtc) {
            if (!canAdd(source, target, out _)) return;
            newState();
            _index.Add(source, target, dtUtc);
        }
        public void OnlyRemoveIfValid(int source, int target) {
            if (!canRemove(source, target, out _)) return;
            newState();
            _index.Remove(source, target);
        }
        public void DeleteIfReferenced(int id) {
            newState();
            _index.DeleteIfReferenced(id);
        }
        public IEnumerable<RelData> GetOtherRelationsThatNeedsToRemovedBeforeAdd(int source, int target) => _index.GetOtherRelationsThatNeedsToRemovedBeforeAdd(source, target);
        public bool Contains(int source, int target) => _index.Contains(source, target);
        public bool Contains(int from, int to, bool fromTargetToSource) => fromTargetToSource ? _index.Contains(to, from) : _index.Contains(from, to);
        public IdSet GetRelated(int id, bool fromTargetToSource) => _index.Get(id, fromTargetToSource);
        public void CompressMemory() => _index.CompressMemory();
        public int MaxCountTo { get; }
        public int MaxCountFrom { get; }
        string getDescription(int source, int target) {
            return $"from " + _store._nodes.Get(source).Id + " and " + _store._nodes.Get(target).Id;
        }
        bool canAdd(int source, int target, [MaybeNullWhen(true)] out string reason) {
            if (!_store._nodes.Contains(source)) { reason = $"Unable to add {source} to the relation {Model}. It does not exist. "; return false; }
            if (!_store._nodes.Contains(target)) { reason = $"Unable to add {target} to the relation {Model}. It does not exist. "; return false; }
            if (!_store._definition.TryGetTypeOfNode(source, out var typeIdFrom)) { reason = $"Unable to add {source} to the relation {Model}. It does not have a valid type. "; return false; }
            if (!_store._definition.TryGetTypeOfNode(target, out var typeIdTo)) { reason = $"Unable to add {target} to the relation {Model}. It does not have a valid type. "; return false; }
            if (!AllSourceTypes.Contains(typeIdFrom)) { reason = $"Relation {Model} does not support from type {_store._definition.NodeTypes[typeIdFrom]}"; return false; }
            if (!AllTargetTypes.Contains(typeIdTo)) { reason = $"Relation {Model} does not support to type {_store._definition.NodeTypes[typeIdTo]}"; return false; }
            if (_index.Contains(source, target)) { reason = $"Relation {getDescription(source, target)} already exists. "; return false; }
            if (_index.CountSource(target) >= MaxCountFrom) { reason = $"Adding relation would violate the max from constraint which is {MaxCountFrom}. Remove existing relations first. "; return false; }
            if (_index.CountTarget(target) >= MaxCountTo) { reason = $"Adding relation would violate the max to constraint which is {MaxCountTo}. Remove existing relations first. "; return false; }
            reason = null;
            return true;
        }
        bool canRemove(int source, int target, [MaybeNullWhen(true)] out string reason) {
            if (!_index.Contains(source, target)) { reason = $"Relation {getDescription(source, target)} does not exists. "; return false; }
            reason = null;
            return true;
        }
        internal void SaveState(IAppendStream stream) => _index.SaveState(stream);
        internal void ReadState(IReadStream stream) => _index.ReadState(stream);
        internal int Count => _index.TotalCount;
        internal IEnumerable<RelData> Values => _index.Values;

    }
}
