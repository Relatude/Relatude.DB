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
        // NodeData
        public static INodeData NodeData(Datamodel datamodel, Stream stream) { // Reading
            var guid = stream.ReadGuid();
            var __id = stream.ReadUInt();
            if (__id != 0) return readVersion_0_Legacy(datamodel, stream, guid, (int)__id);
            var version = (NodeDataVersionFlag)stream.ReadInt();
            __id = stream.ReadUInt();
            if (version == NodeDataVersionFlag.Minimal) return readVersion_1_Minimal(datamodel, stream, guid, (int)__id);
            throw new NotSupportedException("NodeData version " + version + " is not supported. ");
            //return version switch {
            //    NodeDataVersionFlag.Minimal => readVersion_1_Minimal(datamodel, stream, guid),
            //    NodeDataVersionFlag.Access => readVersion_2_Access(datamodel, stream, guid),
            //    NodeDataVersionFlag.Access_Revisions => readVersion_3_Access_Revisions(datamodel, stream, guid),
            //    NodeDataVersionFlag.Access_Language => readVersion_4_Access_Language(datamodel, stream, guid),
            //    NodeDataVersionFlag.Access_Revision_Language => readVersion_4_Access_Revision_Language(datamodel, stream, guid),
            //    _ => throw new NotSupportedException("NodeData version " + version + " is not supported. "),
            //};
        }
        static NodeData readVersion_0_Legacy(Datamodel datamodel, Stream stream, Guid guid, int __id) {
            var nodeTypeId = stream.ReadGuid();
            var collectionId = stream.ReadGuid();
            var lcid = stream.ReadInt();
            var derivedFromLCID = stream.ReadInt();
            var readAccess = stream.ReadGuid();
            var writeAccess = stream.ReadGuid();
            var createdUtc = stream.ReadDateTime();
            var changedUtc = stream.ReadDateTime();
            var valueCount = stream.ReadInt();
            var valueCount2 = stream.ReadInt();
            if (valueCount != valueCount2 || valueCount < 0 || valueCount > 10000) throw new Exception("Binary data corruption. ");
            var values = new Properties<object>(valueCount);
            if (!datamodel.NodeTypes.TryGetValue(nodeTypeId, out var nodeType)) {
                nodeType = datamodel.NodeTypes[Relatude.DB.Datamodels.NodeConstants.BaseNodeTypeId]; // fallback if unknown
            }
            var allProps = nodeType.AllProperties;
            // adding only valid props and force type if needed, adding default for missing:            
            for (int i = 0; i < valueCount; i++) {
                var id = stream.ReadGuid();
                var propType = (PropertyType)stream.ReadUInt();
                var bytes = stream.ReadByteArray();
                var value = toPropertyValue(bytes, propType);
                if (datamodel.Properties.TryGetValue(id, out var propDef)) {
                    if (allProps.ContainsKey(id)) values.Add(id, forceValueType(propDef.PropertyType, value));
                }
            }
            // add defaults for missing props
            if (allProps.Count > values.Count) {
                var missing = allProps.Where(n => !values.ContainsKey(n.Key));
                foreach (var n in missing) {
                    var propDef = datamodel.Properties[n.Key];
                    if (propDef.PropertyType != PropertyType.Relation) {
                        values.Add(n.Key, propDef.GetDefaultValue());
                    }
                }
            }
            return new NodeData(guid, __id, nodeTypeId, 
                //collectionId, lcid, derivedFromLCID, readAccess, writeAccess, 
                createdUtc, changedUtc, values);
        }
        static NodeData readVersion_1_Minimal(Datamodel datamodel, Stream stream, Guid guid, int __id) {
            var nodeTypeId = stream.ReadGuid();
            var createdUtc = stream.ReadDateTime();
            var changedUtc = stream.ReadDateTime();
            var valueCount = stream.ReadInt();
            if (valueCount > 10000) throw new Exception("Binary data corruption. ");
            var values = new Properties<object>(valueCount);
            if (!datamodel.NodeTypes.TryGetValue(nodeTypeId, out var nodeType)) {
                nodeType = datamodel.NodeTypes[Relatude.DB.Datamodels.NodeConstants.BaseNodeTypeId]; // fallback if unknown
            }
            var allProps = nodeType.AllProperties;
            // adding only valid props and force type if needed, adding default for missing:            
            for (int i = 0; i < valueCount; i++) {
                var id = stream.ReadGuid();
                var propType = (PropertyType)stream.ReadUInt();
                var bytes = stream.ReadByteArray();
                var value = toPropertyValue(bytes, propType);
                if (datamodel.Properties.TryGetValue(id, out var propDef)) {
                    if (allProps.ContainsKey(id)) values.Add(id, forceValueType(propDef.PropertyType, value));
                }
            }
            // add defaults for missing props
            if (allProps.Count > values.Count) {
                var missing = allProps.Where(n => !values.ContainsKey(n.Key));
                foreach (var n in missing) {
                    var propDef = datamodel.Properties[n.Key];
                    if (propDef.PropertyType != PropertyType.Relation) {
                        values.Add(n.Key, propDef.GetDefaultValue());
                    }
                }
            }
            return new NodeData(guid, __id, nodeTypeId, 
                //Guid.Empty, 0, 0, Guid.Empty, Guid.Empty, 
                createdUtc, changedUtc, values);
        }
        static NodeData readVersion_2_Access(Datamodel datamodel, Stream stream, Guid guid) {
            throw new NotImplementedException();
        }
        static NodeData readVersion_3_Access_Revisions(Datamodel datamodel, Stream stream, Guid guid) {
            throw new NotImplementedException();
        }
        static NodeData readVersion_4_Access_Language(Datamodel datamodel, Stream stream, Guid guid) {
            throw new NotImplementedException();
        }
        static NodeData readVersion_4_Access_Revision_Language(Datamodel datamodel, Stream stream, Guid guid) {
            throw new NotImplementedException();
        }
        static object toPropertyValue(byte[] bytes, PropertyType propType) {
            return propType switch {
                PropertyType.String => RelatudeDBGlobals.Encoding.GetString(bytes),
                PropertyType.Integer => BitConverter.ToInt32(bytes),
                PropertyType.Long => BitConverter.ToInt64(bytes),
                PropertyType.Guid => new Guid(bytes),
                PropertyType.DateTime => new DateTime(BitConverter.ToInt64(bytes), DateTimeKind.Utc),
                PropertyType.Boolean => BitConverter.ToBoolean(bytes),
                PropertyType.Decimal => DecimalPropertyModel.ToDecimal(bytes),
                PropertyType.StringArray => StringArrayPropertyModel.GetValue(bytes),
                PropertyType.Double => BitConverter.ToDouble(bytes),
                PropertyType.Float => BitConverter.ToSingle(bytes),
                PropertyType.TimeSpan => new TimeSpan(BitConverter.ToInt64(bytes)),
                PropertyType.File => FilePropertyModel.GetValue(bytes),
                PropertyType.ByteArray => bytes,
                PropertyType.FloatArray => FloatArrayPropertyModel.GetValue(bytes),
                _ => throw new NotSupportedException("Reading property type " + propType + " is not supported. "),
            };
        }
        static object forceValueType(PropertyType valueType, object value) {
            if (value is null) throw new Exception("Internal error. Property values cannot be null. ");
            return valueType switch {
                PropertyType.Boolean => BooleanPropertyModel.ForceValueType(value, out _),
                PropertyType.Decimal => DecimalPropertyModel.ForceValueType(value, out _),
                PropertyType.Integer => IntegerPropertyModel.ForceValueType(value, out _),
                PropertyType.Long => LongPropertyModel.ForceValueType(value, out _),
                PropertyType.String => StringPropertyModel.ForceValueType(value, out _),
                PropertyType.StringArray => StringArrayPropertyModel.ForceValueType(value, out _),
                PropertyType.Double => DoublePropertyModel.ForceValueType(value, out _),
                PropertyType.Float => FloatPropertyModel.ForceValueType(value, out _),
                PropertyType.Guid => GuidPropertyModel.ForceValueType(value, out _),
                PropertyType.DateTime => DateTimePropertyModel.ForceValueType(value, out _),
                PropertyType.TimeSpan => TimeSpanPropertyModel.ForceValueType(value, out _),
                PropertyType.ByteArray => ByteArrayPropertyModel.ForceValueType(value, out _),
                PropertyType.FloatArray => FloatArrayPropertyModel.ForceValueType(value, out _),
                PropertyType.File => FilePropertyModel.ForceValueType(value, out _),
                _ => throw new NotSupportedException("It is not possible to force type \"" + valueType + "\". "),
            };
        }

    }
}
