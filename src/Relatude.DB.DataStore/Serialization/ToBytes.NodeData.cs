using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Serialization;

public static partial class ToBytes {
    public static void NodeData(INodeData nodeData, Datamodel datamodel, Stream stream) { // Storing
        var nodeType = datamodel.NodeTypes[nodeData.NodeType];
        if (nodeType.IsComplex()) {
            NodeData_Complex(nodeData, datamodel, stream);
        } else {
            NodeData_Minimal(nodeData, datamodel, stream);

        }
    }
    static void NodeData_Minimal(INodeData nodeData, Datamodel datamodel, Stream stream) {
        stream.WriteGuid(nodeData.Id);
        stream.WriteUInt(0); // indicating newer format version
        stream.WriteInt((int)NodeDataStorageVersions.Minimal);
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
    static void NodeData_Complex(INodeData nodeData, Datamodel datamodel, Stream stream) {
        //if (nodeData is not NodeDataComplex) nodeData = NodeDataComplex.FromMinimal(nodeData);
        stream.WriteGuid(nodeData.Id);
        stream.WriteUInt(0); // indicating newer format version
        stream.WriteInt((int)NodeDataStorageVersions.Complex);
        stream.WriteUInt((uint)nodeData.__Id);
        stream.WriteGuid(nodeData.NodeType);

        //var all0 =
        //    nodeData.ReadAccess == 0
        //    && nodeData.EditViewAccess == 0
        //    && nodeData.CultureId == 0
        //    && nodeData.CollectionId == 0
        //    && nodeData.RevisionId == 0;
        //stream.WriteBool(all0);
        //if (!all0) {
        //    stream.WriteInt(nodeData.ReadAccess);
        //    stream.WriteInt(nodeData.EditViewAccess);
        //    stream.WriteInt(nodeData.CultureId);
        //    stream.WriteInt(nodeData.CollectionId);
        //    stream.WriteInt(nodeData.RevisionId);
        //}

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
            PropertyType.String => RelatudeDBGlobals.Encoding.GetBytes((string)value),
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

