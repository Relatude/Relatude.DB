using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;

namespace Relatude.DB.Serialization;

public static partial class ToBytes {
    public static void NodeData(INodeDataBase n, Datamodel datamodel, Stream stream) { // Storing
        if (n is NodeData nd) {
            nodeDataHeader(nd, datamodel, stream);
            nodeData(nd, datamodel, stream);
        } else if (n is NodeDataRevisions ndMeta) {
            nodeDataHeader(ndMeta, datamodel, stream);
            nodeData_versions(ndMeta, datamodel, stream);
        } else if (n is NodeDataWithRelations ndRel) {
            throw new NotImplementedException("Serialization of NodeDataWithRelations is not implemented yet. ");
        } else {
            throw new NotSupportedException("Node data of type " + n.GetType().FullName + " does not support serialization. ");
        }
    }
    static void nodeDataHeader(INodeData nodeData, Datamodel datamodel, Stream stream) {
        // Header, Id and format version
        stream.WriteGuid(nodeData.Id); // using GUIDs for IDs to ensure consistency accross DB instances
        stream.WriteUInt(0); // indicating newer format version, so legacy readers can skip
        stream.WriteInt((int)NodeDataStorageVersions.NodeData);

        // Node data core data
        stream.WriteUInt((uint)nodeData.__Id); // internal int ID, stored to ensure consistency. But can change accross DB instances.
        stream.WriteGuid(nodeData.NodeType); // using GUIDs for types to ensure consistency accross DB instances
    }
    static void nodeData(NodeData nodeData, Datamodel datamodel, Stream stream) {

        stream.WriteDateTime(nodeData.CreatedUtc); // could be needed dropped, but saves little space
        stream.WriteDateTime(nodeData.ChangedUtc); // needed for sync scenarios

        // Node data meta
        stream.WriteByteArray(INodeMeta.ToBytes(nodeData.Meta));

        // Node data properties
        var allPossibleProps = datamodel.NodeTypes[nodeData.NodeType].AllProperties;
        List<KeyValuePair<PropertyModel, object>> propsToStore = new();
        foreach (var kv in nodeData.Values) {
            if (allPossibleProps.TryGetValue(kv.PropertyId, out var prop) && prop.PropertyType != PropertyType.Relation) {
                // only props that are part of datamodel and not relations
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
    static void nodeData_versions(NodeDataRevisions n, Datamodel datamodel, Stream stream) {
        // Each version
        stream.WriteInt(n.Revisions.Length);
        foreach (var version in n.Revisions) {
            stream.WriteGuid(version.RevisionId);
            stream.WriteUInt((uint)version.RevisionType);
            nodeData(version.Node, datamodel, stream);
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

