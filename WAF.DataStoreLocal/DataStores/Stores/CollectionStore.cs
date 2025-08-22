//using WAF.IO;
//using WAF.Transactions;
//namespace WAF.DataStores.Stores;
//internal class CollectionStore : IDisposable {
//    readonly Dictionary<Guid, StoreCollection> _cuturesById;
//    public CollectionStore() {
//        _cuturesById = new();
//    }
//    public void Add(StoreCollection collection) {
//        _cuturesById.Add(collection.Id, collection);
//    }
//    public void Remove(Guid collectionId) {
//        _cuturesById.Remove(collectionId);
//    }
//    public void Update(StoreCollection collection) {
//        _cuturesById[collection.Id] = collection;
//    }
//    public bool Contains(Guid cultureId) => _cuturesById.ContainsKey(cultureId);
//    public void RegisterAction(ActionBase action) {
//        lock (_lock) {
//            if (action is CollectionAction ca) {
//                switch (ca.Operation) {
//                    case CollectionOperation.Add:
//                        if (ca.Collection == null) throw new Exception("Collection is null. ");
//                        Add(ca.Collection);
//                        break;
//                    case CollectionOperation.Remove:
//                        if (ca.CollectionToRemoveId == default) throw new Exception("CollectionToRemoveId is not set. ");
//                        Remove(ca.CollectionToRemoveId);
//                        break;
//                    case CollectionOperation.Update:
//                        if (ca.Collection == null) throw new Exception("Collection is null. ");
//                        Update(ca.Collection);
//                        break;
//                    default: throw new NotImplementedException();
//                }
//            }
//        }
//    }
//    static Guid _startMarker = new Guid("fc6767fc-903e-4ddb-a75a-92dc4fd1167f");
//    static Guid _endMarker = new Guid("3cf56739-4c6b-4a96-aedf-5b9f36ee848d");
//    public void SaveState(IAppendStream stream) {
//        stream.WriteGuid(_startMarker);
//        stream.WriteVerifiedInt(_cuturesById.Count);
//        var ms = new MemoryStream();
//        foreach (var collection in _cuturesById.Values) collection.AppendStream(ms);
//        stream.WriteByteArray(ms.ToArray());
//        stream.WriteGuid(_endMarker);
//    }
//    public void ReadState(IReadStream stream) {
//        stream.ValidateMarker(_startMarker);
//        var noCults = stream.ReadVerifiedInt();
//        var ms = new MemoryStream(stream.ReadByteArray());
//        stream.ValidateMarker(_endMarker);
//        for (int i = 0; i < noCults; i++) Add(StoreCollection.FromStream(ms));
//    }
//    public void Dispose() {
//    }
//}
