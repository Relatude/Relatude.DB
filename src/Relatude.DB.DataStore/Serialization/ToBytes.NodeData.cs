using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Serialization;

public static partial class ToBytes {
    public static void NodeData(INodeData nodeData, Datamodel datamodel, Stream stream) { // Storing
        if (nodeData is NodeData nd) {
            if (nd.Meta == null) {
                NodeData_Minimal(nd, datamodel, stream, NodeDataStorageVersions.Minimal);
            } else {
                NodeData_MinimalMeta(nd, datamodel, stream);
            }
        } else if (nodeData is NodeDataVersion ndMeta) {
            NodeData_Meta(ndMeta, datamodel, stream);
        } else if (nodeData is NodeDataWithRelations ndRel) {
            NodeData_Relations(ndRel, datamodel, stream);
        } else {
            throw new NotSupportedException("Node data of type " + nodeData.GetType().FullName + " does not support serialization. ");
        }
    }
    static void NodeData_Minimal(NodeData nodeData, Datamodel datamodel, Stream stream, NodeDataStorageVersions version) {
        stream.WriteGuid(nodeData.Id);
        stream.WriteUInt(0); // indicating newer format version
        stream.WriteInt((int)version);
        stream.WriteUInt((uint)nodeData.__Id);
        stream.WriteGuid(nodeData.NodeType);
        stream.WriteDateTime(nodeData.CreatedUtc);
        stream.WriteDateTime(nodeData.ChangedUtc);
        var allPossibleProps = datamodel.NodeTypes[nodeData.NodeType].AllProperties;
        List<KeyValuePair<PropertyModel, object>> propsToStore = new();
        int metaPropsCount = 0;

        Guid collectionId = Guid.Empty;
        Guid readAccess = Guid.Empty;
        Guid writeAccess = Guid.Empty;

        foreach (var kv in nodeData.Values) {
            if (allPossibleProps.TryGetValue(kv.PropertyId, out var prop) && prop.PropertyType != PropertyType.Relation) {
                // only props that are part of datamodeland not relations
                propsToStore.Add(new(prop, kv.Value));
            } else if (kv.PropertyId == NodeConstants.SystemReadAccessPropertyId) {
                readAccess = (Guid)kv.Value;
                metaPropsCount++;
            } else if (kv.PropertyId == NodeConstants.SystemWriteAccessPropertyId) {
                writeAccess = (Guid)kv.Value;
                metaPropsCount++;
            } else if (kv.PropertyId == NodeConstants.SystemCollectionPropertyId) {
                collectionId = (Guid)kv.Value;
                metaPropsCount++;
            }
        }
        stream.WriteInt(propsToStore.Count + metaPropsCount);
        foreach (var p in propsToStore) {
            var bytes = serializePropertyValue(p.Value, p.Key.PropertyType);
            stream.WriteGuid(p.Key.Id);// prop ID
            stream.WriteUInt((uint)p.Key.PropertyType);// prop type
            stream.WriteByteArray(bytes); // data
        }
        // write meta props
        if (readAccess != Guid.Empty) {
            stream.WriteGuid(NodeConstants.SystemReadAccessPropertyId);
            stream.WriteUInt((uint)PropertyType.Guid);
            stream.WriteByteArray(serializePropertyValue(readAccess, PropertyType.Guid));
        }
        if (writeAccess != Guid.Empty) {
            stream.WriteGuid(NodeConstants.SystemWriteAccessPropertyId);
            stream.WriteUInt((uint)PropertyType.Guid);
            stream.WriteByteArray(serializePropertyValue(writeAccess, PropertyType.Guid));
        }
        if (collectionId != Guid.Empty) {
            stream.WriteGuid(NodeConstants.SystemCollectionPropertyId);
            stream.WriteUInt((uint)PropertyType.Guid);
            stream.WriteByteArray(serializePropertyValue(collectionId, PropertyType.Guid));
        }
    }
    static void NodeData_MinimalMeta(NodeData nodeData, Datamodel datamodel, Stream stream) {
        NodeData_Minimal(nodeData, datamodel, stream, NodeDataStorageVersions.WithMinimalMeta);
        stream.WriteByteArray(nodeData.Meta!.ToBytes());
    }
    static void NodeData_Meta(NodeDataVersion nodeData, Datamodel datamodel, Stream stream) {
        throw new Exception("Not implemented yet.");
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
    static void NodeData_Relations(NodeDataWithRelations nodeData, Datamodel datamodel, Stream stream) {
        throw new Exception("Not implemented yet.");
        // same as Minimal but with relations serialized too
    }
}

