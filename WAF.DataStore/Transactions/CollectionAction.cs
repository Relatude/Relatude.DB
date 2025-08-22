using WAF.Datamodels;
using WAF.DataStores;

namespace WAF.Transactions {
    public enum CollectionOperation : byte {
        Add = 0,
        Remove = 1,
        Update = 2,
    }
    public class CollectionAction : ActionBase {
        public static CollectionAction Add(StoreCollection collection) => new(CollectionOperation.Add, collection);
        public static CollectionAction Remove(Guid id) => new(CollectionOperation.Remove, id);
        public static CollectionAction Update(StoreCollection collection) => new(CollectionOperation.Update, collection);
        private CollectionAction(CollectionOperation operation, Guid id)
            : base(ActionTarget.Collection) {
            Operation = operation;
            CollectionToRemoveId = id;
        }
        private CollectionAction(CollectionOperation operation, StoreCollection? collection, StoreCollection? oldCollection = null)
            : base(ActionTarget.Collection) {
            Operation = operation; Collection = collection; OldUpdatedCollection = oldCollection;
        }
        public CollectionOperation Operation { get; }
        public Guid CollectionToRemoveId { get; }
        public StoreCollection? Collection { get; }
        public StoreCollection? OldUpdatedCollection { get; }
        public override string ToString() {
            switch (Operation) {
                case CollectionOperation.Add:
                    return $"Add collection {Collection}. ";
                case CollectionOperation.Remove:
                    return $"Remove collection {CollectionToRemoveId}. ";
                case CollectionOperation.Update:
                    return $"Update collection {OldUpdatedCollection} to {Collection}. ";
                default:
                    throw new NotImplementedException();
            }
        }
        public override string OperationName() => "Collection." + Operation.ToString();
        public override string ToString(Datamodel dm) => ToString();
        //public override ActionBase Opposite() {
        //    switch (Operation) {
        //        case CollectionOperation.Add:
        //            return new CollectionAction(CollectionOperation.Remove, Collection);
        //        case CollectionOperation.Remove:
        //            return new CollectionAction(CollectionOperation.Add, Collection);
        //        case CollectionOperation.Update:
        //            if (OldUpdatedCollection == null) throw new Exception("OldCollection is null. ");
        //            return new CollectionAction(CollectionOperation.Update, OldUpdatedCollection, Collection);
        //        default:
        //            throw new NotImplementedException();
        //    }
        //}
    }
}
