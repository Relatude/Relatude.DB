using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query.Data;
using Relatude.DB.Transactions;
using System.Text;

namespace Relatude.DB.Serialization;
public static partial class ToBytes {
    public static int DefaultPageSize = 10; // default page size if  not specified
    static PropertyType determineValuePropType(object value) {
        if (value is int) return PropertyType.Integer;
        if (value is string) return PropertyType.String;
        if (value is double) return PropertyType.Double;
        if (value is float) return PropertyType.Float;
        if (value is string[]) return PropertyType.StringArray;
        if (value is bool) return PropertyType.Boolean;
        if (value is DateTime) return PropertyType.DateTime;
        if (value is TimeSpan) return PropertyType.TimeSpan;
        if (value is Guid) return PropertyType.Guid;
        if (value is decimal) return PropertyType.Decimal;
        if (value is long) return PropertyType.Long;
        if (value is byte[]) return PropertyType.ByteArray;
        if (value is float[]) return PropertyType.FloatArray;
        throw new NotSupportedException("Only value types supported. ");
    }
    static void writeValueType(object v, Stream stream, PropertyType propType = PropertyType.Any) {
        if (propType == PropertyType.Any) propType = determineValuePropType(v);
        stream.WriteUInt((uint)propType);
        switch (propType) {
            case PropertyType.Boolean: stream.WriteBool((bool)v); break;
            case PropertyType.Integer: stream.WriteInt((int)v); break;
            case PropertyType.Double: stream.WriteDouble((double)v); break;
            case PropertyType.Float: stream.WriteFloat((float)v); break;
            case PropertyType.String: stream.WriteString((string)v); break;
            case PropertyType.StringArray: stream.WriteStringArray((string[])v); break;
            case PropertyType.DateTime: stream.WriteDateTime((DateTime)v); break;
            case PropertyType.TimeSpan: stream.WriteTimeSpan((TimeSpan)v); break;
            case PropertyType.Guid: stream.WriteGuid((Guid)v); break;
            case PropertyType.Decimal: stream.WriteDecimal((decimal)v); break;
            case PropertyType.Long: stream.WriteLong((long)v); break;
            case PropertyType.ByteArray: stream.WriteByteArray((byte[])v); break;
            case PropertyType.FloatArray: stream.WriteFloatArray((float[])v); break;

            case PropertyType.Any:
            case PropertyType.Relation:
            //case PropertyType.Collection:
            //case PropertyType.DataObject:
            //case PropertyType.FacetCollection:
            //case PropertyType.FacetNumberRange:
            default:
                throw new NotSupportedException("Only value types supported. ");
        }
    }
    //public static void collectionData<T>(IStoreNodeDataCollection source, IEnumerable<T> collection, Action<T> buildRow, Datamodel datamodel, Stream stream) {
    //    stream.Write(source.TotalCount);
    //    stream.Write(source.DurationMs);
    //    stream.Write(source.PageIndexUsed);
    //    if (source.PageSizeUsed.HasValue) {
    //        stream.Write(true);
    //        stream.Write(source.PageSizeUsed.Value);
    //    } else {
    //        stream.Write(false);
    //    }
    //    stream.Write(source.TotalCount);
    //    stream.Write(source.Values.Count());
    //    int count;
    //    var pageSize = source.PageSizeUsed.HasValue && source.PageSizeUsed.Value > 0 ? source.PageSizeUsed.Value : DefaultPageSize;
    //    var pageCount = (int)Math.Ceiling((double)source.TotalCount / pageSize);
    //    if (source.TotalCount > 0) { // counts on page
    //        var skip = pageSize * source.PageIndexUsed;
    //        if (skip > source.TotalCount) {
    //            count = 0;
    //        } else {
    //            count = (skip + pageSize) > source.TotalCount ? source.TotalCount - skip : pageSize;
    //        }
    //    } else {
    //        count = source.TotalCount;
    //    }
    //    var i = 0;
    //    stream.Write(count);
    //    foreach (var item in collection) {
    //        if (++i > count) break;
    //        buildRow(item);
    //    }
    //}

    static void nodeAction(NodeAction action, Datamodel def, Stream stream, out long nodeSegmentRelativeOffset, out int nodeSegmentLength) {
        StreamExtenstions.WriteOneByte(stream, (byte)action.Operation);
        nodeSegmentRelativeOffset = stream.Position;
        NodeData(action.Node, def, stream);
        long length = stream.Position - nodeSegmentRelativeOffset; // max size of one node is therefore 4GB!
        if (length > int.MaxValue) throw new Exception("Node data exceeds max size of 4GB");
        nodeSegmentLength = (int)length;
    }
    static void relationAction(RelationAction action, Datamodel def, Stream stream, out long nodeSegmentRelativeOffset, out int nodeSegmentLength) {
        nodeSegmentRelativeOffset = 0; nodeSegmentLength = 0; // not relevant for relations
        StreamExtenstions.WriteOneByte(stream, (byte)action.Operation);
        stream.WriteGuid(action.RelationId);
        stream.WriteGuid(action.SourceGuid);
        stream.WriteGuid(action.TargetGuid);
        stream.WriteUInt((uint)action.Source);
        stream.WriteUInt((uint)action.Target);
        stream.WriteDateTime(action.ChangeUtc);
        // stream.WriteBool(action.EnsuringCausedChange); // not relevant for writing and not serialized
    }
    static void collectionAction(CollectionAction action, Stream stream, out long nodeSegmentRelativeOffset, out int nodeSegmentLength) {
        nodeSegmentRelativeOffset = 0; nodeSegmentLength = 0; // not relevant for collections
        StreamExtenstions.WriteOneByte(stream, (byte)action.Operation);
        if (action.Operation != CollectionOperation.Remove) {
            if (action.CollectionToRemoveId == Guid.Empty) throw new Exception("Internal error. ");
            stream.WriteGuid(action.CollectionToRemoveId);
        } else { // Add or Update
            if (action.Collection == null) throw new Exception("Internal error. ");
            using var ms = new MemoryStream();
            action.Collection.AppendStream(ms);
            stream.WriteByteArray(ms.ToArray());
        }
    }

    public static void ActionBaseList(List<ActionBase> actions, Datamodel datamodel, MemoryStream ms) {
        ms.WriteInt(actions.Count);
        foreach (var action in actions) ActionBase(action, datamodel, ms, out _, out _);
    }
    public static void ActionBase(ActionBase action, Datamodel def, Stream stream, out long nodeSegmentRelativeOffset, out int nodeSegmentLength) {
        StreamExtenstions.WriteOneByte(stream, (byte)action.ActionTarget);
        if (action is NodeAction na) nodeAction(na, def, stream, out nodeSegmentRelativeOffset, out nodeSegmentLength);
        else if (action is RelationAction ra) relationAction(ra, def, stream, out nodeSegmentRelativeOffset, out nodeSegmentLength);
        else if (action is CollectionAction cta) collectionAction(cta, stream, out nodeSegmentRelativeOffset, out nodeSegmentLength);
        else throw new NotSupportedException();        
    }
    public static void FacetQueryResultData(FacetQueryResultData fq, Datamodel datamodel, Stream stream) {
        stream.WriteInt(fq.SourceCount);
        stream.WriteDouble(fq.DurationMs);
        stream.WriteInt(fq.Facets.Count);
        foreach (var f in fq.Facets.Values) {
            stream.WriteGuid(f.PropertyId);
            stream.WriteString(f.DisplayName);
            stream.WriteString(f.CodeName + "");
            stream.WriteInt(f.Values.Count);
            foreach (var v in f.Values) {
                stream.WriteString(v.DisplayName);
                stream.WriteInt(v.Count);
                writeValueType(v.Value, stream);
                if (v.Value2 == null) {
                    stream.WriteBool(false);
                } else {
                    stream.WriteBool(true);
                    writeValueType(v.Value2, stream);
                }
            }
        }
        //collectionData(fq.Result, (IStoreNodeDataCollection)fq.Result.Page(pageIndex,pageSize), datamodel, stream);
    }
    public static void ObjectToBytes(object data, Datamodel datamodel, Stream stream) {
        throw new NotImplementedException();
    }
}

