using Relatude.DB.Datamodels;
using Relatude.DB.DataStores.Definitions;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;
internal class NodeTypesByIdsWithMeta {
    readonly Definition _definition;
    //readonly List<NodeMeta> _metas = [];
    public NodeTypesByIdsWithMeta(Definition definition) {
        _definition = definition;
    }
    internal void Insert(INodeData node, NodeTypeModel nodeType) {
        throw new NotImplementedException();
    }
    internal void Delete(INodeData node, NodeTypeModel nodeType) {
        throw new NotImplementedException();
    }
    internal IdSet GetAllNodeIdsForType(NodeTypeModel typeDef, QueryContext ctx) {
        throw new NotImplementedException();
    }
    internal IdSet GetAllNodeIdsForTypeNoAccessControl(NodeTypeModel typeDef, bool excludeDecendants) {
        throw new NotImplementedException();
    }
    internal bool TryGetType(int id, out Guid typeId) {
        throw new NotImplementedException();
    }
    internal void SaveState(IAppendStream stream) {
        // throw new NotImplementedException();
    }
    internal void ReadState(IReadStream stream) {
        // throw new NotImplementedException();
    }
}
//// QueryContext + typeId (-> userId -> groups ->) MetaKey[]
//// NodeMeta -> MetaKey

//using Relatude.DB.Datamodels;
//using Relatude.DB.DataStores.Indexes;
//using Relatude.DB.DataStores.Indexes.Trie.TrieNet._Ukkonen;
//using Relatude.DB.DataStores.Sets;

//class/struct MetaKey : IComparable {
//    Guid TypeId

//    ReadGroup
//    AccessGroup
//    ....
//}
//// ( Not DateTime ) - properties that differ a lot.
//// The idea is that there should not be too many unique combinations of MetaKeys
//// as they are all stored in mem, an internal max


//MetaKey[] GetKeysFromContext(QueryContext ctx) { }
//MetaKey GetKeyFromNode(NodeDataComplex ctx) { }
//void IsComplex(nodeId)
//void Add(NodeDataComplex node)
//void Remove(NodeDataComplex node)
//IdSet GetNodes(int typeId, QueryContext ctx) {
//    // get meta keys from context
//    // get 
//    // perform intersecions using setregister with IValueIndexes
//}
//Dictionary<MetaKey, int[]> _nodeIdsByMetaKey;

//IValueIndex<DateTime> _cache;
//IValueIndex<DateTime> _cache;
//IValueIndex<DateTime> _cache;
//IValueIndex<DateTime> _cache;





















//using Relatude.DB.Common;
//using Relatude.DB.Datamodels;
//using Relatude.DB.DataStores.Indexes;
//using Relatude.DB.DataStores.Sets;
//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;

//namespace Relatude.DB.DataStores.Stores;
//internal class NodeMetaIndex {
//    public NodeMetaIndex(Func<IValueIndex<Guid>> valueIndexFactory) {
//        _readAccess = valueIndexFactory();
//        _editViewAccess = valueIndexFactory();
//        _editWriteAccess = valueIndexFactory();
//        _publishAccess = valueIndexFactory();
//    }
//    public IdSet GetAllNodeIdsForType(Guid typeId, QueryContext ctx) {
//        List<Guid> _userMemberships = [];
//        var ids = _readAccess.FilterInValues(IdSet.AllIds, _userMemberships);

//    }
//}
////public class NodeMeta {

////    public bool IsDeleted { get; set; }
////    public bool IsHidden{ get; set; }
////    public string? CultureCode { get; }
////    public bool IsFallbackCulture { get; set; }
////    public int RevisionId { get; set; }

////    public Guid ReadAccessId { get; set; } // hard read access for nodes in any context
////    public Guid EditViewAccessId { get; set; } // soft filter for to show or hide nodes in the edit ui
////    public Guid EditWriteAccessId { get; set; } // control access to edit unpublished revisions and request publication/depublication
////    public Guid PublishAccessId { get; set; } // control access to change live publish or depublish revisions


////    public DateTime ChangedUtc { get; }
////    public DateTime CreatedUtc { get; set; }
////    public DateTime PublishedUtc { get; }
////    public DateTime RetainedUtc { get; set; }
////    public DateTime ReleasedUtc { get; set; }


////}