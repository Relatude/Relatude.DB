using WAF.Common;
using WAF.Datamodels;
using WAF.Datamodels.Properties;
using WAF.Query.Data;
using WAF.Transactions;
using System.Text;

namespace WAF.Serialization;
public static partial class ToBytes {
    public static void NodeData(INodeData nodeData, Datamodel datamodel, Stream stream) { // Storing
        //NodeData_Legacy(nodeData, datamodel, stream);
        NodeData_Minimal(nodeData, datamodel, stream);
    }
    static void NodeData_Legacy(INodeData nodeData, Datamodel datamodel, Stream stream) { 
        stream.WriteGuid(nodeData.Id);
        stream.WriteUInt((uint)nodeData.__Id);
        stream.WriteGuid(nodeData.NodeType);
        //stream.WriteGuid(nodeData.CollectionId);
        //stream.WriteInt(nodeData.LCID);
        //stream.WriteInt(nodeData.DerivedFromLCID);
        //stream.WriteGuid(nodeData.ReadAccess);
        //stream.WriteGuid(nodeData.WriteAccess);
        stream.WriteDateTime(nodeData.CreatedUtc);
        stream.WriteDateTime(nodeData.ChangedUtc);
        var allPossibleProps = datamodel.NodeTypes[nodeData.NodeType].AllProperties;
        List<KeyValuePair<PropertyModel, object>> propsToStore = new();
        foreach (var kv in nodeData.Values) {
            if (allPossibleProps.TryGetValue(kv.PropertyId, out var prop) && prop.PropertyType != PropertyType.Relation) {
                // only props that are part of datamodeland not relations
                propsToStore.Add(new(prop, kv.Value));
            }
        }
        stream.WriteInt(propsToStore.Count);
        stream.WriteInt(propsToStore.Count);  // written twice for some verification later
        foreach (var p in propsToStore) {
            var bytes = serializePropertyValue(p.Value, p.Key.PropertyType);
            stream.WriteGuid(p.Key.Id);// prop ID
            if ((int)p.Key.PropertyType == 0) throw new Exception("Internal serialization error. ");
            stream.WriteUInt((uint)p.Key.PropertyType);// prop type
            stream.WriteByteArray(bytes); // data
        }
    }
    static void NodeData_Minimal(INodeData nodeData, Datamodel datamodel, Stream stream) {
        stream.WriteGuid(nodeData.Id);
        stream.WriteUInt(0);
        stream.WriteInt((int)NodeDataVersionFlag.Minimal);
        stream.WriteUInt((uint)nodeData.__Id);
        stream.WriteGuid(nodeData.NodeType);
        stream.WriteDateTime(nodeData.CreatedUtc);
        stream.WriteDateTime(nodeData.ChangedUtc);
        var allPossibleProps = datamodel.NodeTypes[nodeData.NodeType].AllProperties;
        List<KeyValuePair<PropertyModel, object>> propsToStore = new();
        foreach (var kv in nodeData.Values) {
            if (allPossibleProps.TryGetValue(kv.PropertyId, out var prop) && prop.PropertyType != PropertyType.Relation) {
                // only props that are part of datamodeland not relations
                propsToStore.Add(new(prop, kv.Value));
            }
        }
        stream.WriteInt(propsToStore.Count);
        foreach (var p in propsToStore) {
            var bytes = serializePropertyValue(p.Value, p.Key.PropertyType);
            stream.WriteGuid(p.Key.Id);// prop ID
            stream.WriteUInt((uint)p.Key.PropertyType);// prop type
            stream.WriteByteArray(bytes); // data
        }
    }
    static byte[] serializePropertyValue(object value, PropertyType propType) {
        return propType switch {
            PropertyType.Integer => BitConverter.GetBytes((int)value),
            PropertyType.Guid => ((Guid)value).ToByteArray(),
            PropertyType.String => WAFGlobals.Encoding.GetBytes((string)value),
            PropertyType.DateTime => BitConverter.GetBytes(((DateTime)value).Ticks),
            PropertyType.Boolean => BitConverter.GetBytes((bool)value),
            PropertyType.Decimal => DecimalPropertyModel.ToBytes((decimal)value),
            PropertyType.Long => BitConverter.GetBytes((long)value),
            PropertyType.ByteArray => (byte[])value,
            PropertyType.FloatArray => FloatArrayPropertyModel.GetBytes((float[])value),
            PropertyType.TimeSpan => BitConverter.GetBytes(((TimeSpan)value).Ticks),
            PropertyType.Double => BitConverter.GetBytes((double)value),
            PropertyType.Float => BitConverter.GetBytes((float)value),
            PropertyType.StringArray => StringArrayPropertyModel.GetBytes((string[])value),
            PropertyType.File => FilePropertyModel.GetBytes((FileValue)value),
            _ => throw new NotSupportedException("Writing property type " + propType + " is not supported. "),
        };
    }
}

