//using Relatude.DB.Common;
//using Relatude.DB.DataStores.Sets;
//using Relatude.DB.IO;

//namespace Relatude.DB.DataStores.Indexes {
//    public class StringSetIndex : IndexBase {
//        readonly IdSetByValue<string> _nodeIdByValue;
//        readonly ValueByIdIndex<string> _valueByNodeId;
//        public StringSetIndex(string uniqueKey, Guid propertyId) : base(uniqueKey, propertyId) {
//            _nodeIdByValue = new(uniqueKey);
//            _valueByNodeId = new();
//        }
//        public IdSet Filter(IdSet set, IndexOperator op, string value) {
//            var matches = _nodeIdByValue.Get(value);
//            if (op == IndexOperator.Equal) return set.Intersection(matches);
//            if (op == IndexOperator.NotEqual) return set.DisjunctiveUnion(matches);
//            throw new NotSupportedException("The string index does not support the " + op.ToString().ToUpper() + " operator. ");
//        }
//        public IdSet ReOrder(IdSet set, bool descending) => set.ReOrder(_valueByNodeId, descending);
//        public int CountEqual(IdSet set, string value) {
//            return set.CountIntersection(_nodeIdByValue.Get(value));
//        }
//        public void IndexValue(int nodeId, object value) {
//            var v = (string)value;
//            _valueByNodeId.Add(nodeId, v);
//            _nodeIdByValue.Index(nodeId, v);
//        }
//        public override void DeIndexValue(int nodeId, object value) {
//            var v = (string)value;
//            _valueByNodeId.Remove(nodeId);
//            _nodeIdByValue.DeIndex(nodeId, v);
//        }
//        public bool ContainsValue(string value) => _nodeIdByValue.ContainsValue(value);
//        public IEnumerable<string> GetUniqueValues() {
//            return _nodeIdByValue.UniqueValues;
//        }
//        public int MaxCount(IndexOperator op, string value) {
//            switch (op) {
//                case IndexOperator.Equal:
//                    return 1;
//                case IndexOperator.NotEqual:
//                case IndexOperator.Greater:
//                case IndexOperator.Smaller:
//                case IndexOperator.GreaterOrEqual:
//                case IndexOperator.SmallerOrEqual:
//                    return _valueByNodeId.Count;
//                default: break;
//            }
//            throw new NotSupportedException("Integer types does not support the " + op.ToString().ToUpper() + " operator. ");
//        }
//        public IdSet FilterInValues(IdSet set, List<string> values) {
//            return set.Filter(nodeId => {
//                var v = _valueByNodeId[nodeId];
//                foreach (var value in values) {
//                    if (v == value) return true;
//                }
//                return false;
//            });
//        }
//        public override void SaveState(IAppendStream stream) {
//            _nodeIdByValue.SaveState(stream, stream.WriteString);
//            stream.WriteVerifiedInt(_valueByNodeId.Count);
//            foreach (var kv in _valueByNodeId) {
//                stream.WriteUInt(kv.Key);
//                stream.WriteString(kv.Value);
//            }
//        }
//        public override void ReadState(IReadStream stream) {
//            _nodeIdByValue.ReadState(stream, stream.ReadString);
//            var count_valueByNodeId = stream.ReadVerifiedInt();
//            for (var i = 0; i < count_valueByNodeId; i++) {
//                var k = stream.ReadUInt();
//                var v = stream.ReadString();
//                _valueByNodeId.Add(k, v);
//            }
//        }
//        public override void CompressMemory() {
//        }
//        public override void Dispose() { }
//        public override void ClearCache() { }
//    }
//}
