using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query.Data;
using Relatude.DB.Transactions;
using System.Text;
using Relatude.DB.DataStores;
using System;
using System.Drawing;

namespace Relatude.DB.Serialization {
    public static partial class FromBytes {

        // Actions
        //public static List<ActionBase> ActionBaseList(Datamodel datamodel, Stream stream) {
        //    var length = stream.ReadInt();
        //    var actions = new List<ActionBase>(length);
        //    for (int i = 0; i < length; i++) actions.Add(ActionBase(datamodel, stream, out _, out _));
        //    return actions;
        //}
        //public static ActionBase ActionBase(Datamodel datamodel, Stream stream, out long nodeSegmentRelativeOffset, out int nodeSegmenLength) {
        //    var target = (ActionTarget)stream.ReadOneByte();
        //    if (target == ActionTarget.Node) return nodeAction(datamodel, stream, out nodeSegmentRelativeOffset, out nodeSegmenLength);
        //    nodeSegmentRelativeOffset = 0; nodeSegmenLength = 0; // not relevant for relations
        //    if (target == ActionTarget.Relation) return relationAction(stream);
        //    throw new NotImplementedException();
        //}
        //static NodeAction nodeAction(Datamodel datamodel, Stream stream, out long nodeSegmentRelativeOffset, out int nodeSegmentLength) {
        //    var operation = (NodeOperation)stream.ReadOneByte();
        //    nodeSegmentRelativeOffset = stream.Position;
        //    var nodeData = NodeData(datamodel, stream);
        //    long length = stream.Position - nodeSegmentRelativeOffset;
        //    if (length > int.MaxValue) throw new Exception("Node data exceeds max size of 4GB");
        //    nodeSegmentLength = (int)length;
        //    return NodeAction.Load(operation, nodeData);
        //}
        static RelationAction relationAction(Stream stream) {
            var operation = (RelationOperation)stream.ReadOneByte();
            var relationId = stream.ReadGuid();
            var r = new RelationAction(operation, relationId);
            r.SourceGuid = stream.ReadGuid();
            r.TargetGuid = stream.ReadGuid();
            r.Source = (int)stream.ReadUInt();
            r.Target = (int)stream.ReadUInt();
            r.ChangeUtc = stream.ReadDateTime();
            return r;
        }
        // object
        public static object ObjectFromBytes(Datamodel datamodel, Stream stream) {
            var dataType = (DataType)stream.ReadByte();
            return dataType switch {
                DataType.ValueTypeData => valueData(stream),
                DataType.ValueCollectionData => ValueCollectionData.DeSerialize(datamodel, stream),
                DataType.ObjectCollection => ObjectCollection.DeSerialize(datamodel, stream),
                DataType.ObjectData => ObjectData.DeSerialize(datamodel, stream),
                DataType.TableData => TableData.DeSerialize(datamodel, stream),
                DataType.IStoreNodeData => storeNodeData(datamodel, stream),
                DataType.IStoreNodeDataCollection => storeNodeDataCollection(datamodel, stream),
                _ => throw new NotImplementedException(),
            };
        }
        static object valueData(Stream stream) {
            var propertyType = (PropertyType)stream.ReadUInt();
            return propertyType switch {
                PropertyType.Boolean => stream.ReadBool(),
                PropertyType.Integer => stream.ReadInt(),
                PropertyType.String => stream.ReadString(),
                PropertyType.DateTime => stream.ReadDateTime(),
                PropertyType.DateTimeOffset => stream.ReadDateTimeOffset(),
                PropertyType.TimeSpan => stream.ReadTimeSpan(), 
                PropertyType.Double => stream.ReadDouble(),
                PropertyType.Float => stream.ReadFloat(),
                _ => throw new NotImplementedException(),
            };
        }
        static IStoreNodeData storeNodeData(Datamodel datamodel, Stream stream) {
            throw new NotImplementedException();
        }
        static IStoreNodeDataCollection storeNodeDataCollection(Datamodel datamodel, Stream stream) {
            throw new NotImplementedException();
        }

        public static long Long(Stream response) {
            return response.ReadLong();
        }
    }
}
