//using Relatude.DB.Common;
//using Relatude.DB.Datamodels;
//using Relatude.DB.Datamodels.Properties;
//using Relatude.DB.Query.Data;
//using System.Text;
//using System.Text.Json;
//using Relatude.DB.DataStores;

//namespace Relatude.DB.Serialization;
//public static class ToJson {
//    public static int DefaultPageSize = 10;  // default page size if  not specified
//    public static void StoreNodeData(IStoreNodeData snd, Datamodel dm, StringBuilder sb) {
//        NodeData(snd.NodeData, dm, sb);
//    }
//    public static void NodeData(INodeData nd, Datamodel dm, StringBuilder sb) {
//        sb.Append("{\"id\":");
//        ValueType(nd.Id, sb);
//        sb.Append(",\"info\":{");
//        sb.Append("\"createdUtc\":");
//        ValueType(nd.CreatedUtc, sb);
//        sb.Append(",\"changedUtc\":");
//        ValueType(nd.ChangedUtc, sb);
//        sb.Append(",\"nodeType\":");
//        ValueType(nd.NodeType, sb);
//        sb.Append("},");
//        var count = nd.ValueCount;
//        foreach (var kv in nd.Values) {
//            sb.Append('\"');
//            sb.Append(DefaultCamelCasing(dm.Properties[kv.PropertyId].CodeName));
//            sb.Append("\":");
//            ValueType(kv.Value, sb);
//            if (--count > 0) sb.Append(',');
//        }
//        sb.Append('}');
//    }
//    public static void TableData(TableData td, StringBuilder sb) {
//        collectionData(sb, td, td.Rows, (row) => serializeRow(sb, td.Properties, row), td.DurationMs);
//    }
//    public static void ObjectCollection(ObjectCollection td, StringBuilder sb) {
//        collectionData(sb, td, td.Values, (row) => Generic(row, sb), td.DurationMs);
//    }
//    public static void ObjectData(ObjectData od, StringBuilder sb) {
//        serializeRow(sb, od.Properties, od.Values);
//    }
//    public static void StoreInfo(StoreStatus od, StringBuilder sb) {
//        string json = JsonSerializer.Serialize(od, _defaultOptions);
//        sb.Append(json);
//    }
//    public static void StoreNodeDataCollection(IStoreNodeDataCollection snd, Datamodel dm, StringBuilder sb) {
//        collectionData(sb, snd, snd.NodeValues, (nodeData) => {
//            ToJson.NodeData(nodeData, dm, sb);
//        }, snd.DurationMs);
//    }
//    public static void ValueCollectionData(ValueCollectionData vcd, StringBuilder sb) {
//        sb.Append('[');
//        var r = 0;
//        foreach (var v in vcd.Values) {
//            if (v is null) throw new NullReferenceException();
//            if (r++ > 0) sb.Append(',');
//            ValueType(v, sb);
//        }
//        sb.Append(']');
//    }
//    static void collectionData<T>(StringBuilder sb, ICollectionData source, IEnumerable<T> collection, Action<T> buildRow, double durationsMs) {
//        sb.Append('{');
//        sb.Append("\"totalCount\":" + source.TotalCount + ",");
//        sb.Append("\"durationMs\":");
//        ValueType(durationsMs, sb);
//        sb.Append(',');
//        var pageSize = source.PageSizeUsed.HasValue && source.PageSizeUsed.Value > 0 ? source.PageSizeUsed.Value : DefaultPageSize;
//        var pageIndex = source.PageIndexUsed;
//        var pageCount = (int)Math.Ceiling((double)source.TotalCount / pageSize);
//        sb.Append("\"pageSize\":" + pageSize + ",");
//        sb.Append("\"pageIndex\":" + pageIndex + ",");
//        sb.Append("\"pageCount\":" + pageCount + ",");
//        int count;
//        if (source.TotalCount > 0) { // counts on page
//            var skip = pageSize * pageIndex;
//            if (skip > source.TotalCount) {
//                count = 0;
//            } else {
//                count = (skip + pageSize) > source.TotalCount ? source.TotalCount - skip : pageSize;
//            }
//        } else {
//            count = source.TotalCount;
//        }
//        sb.Append("\"count\":" + count + ",");
//        sb.Append("\"isAll\":" + (count == source.TotalCount ? "true" : "false") + ",");
//        sb.Append("\"isLastPage\":" + (pageIndex + 1 >= pageCount ? "true" : "false") + ",");
//        sb.Append("\"values\":");
//        sb.Append('[');
//        var i = 0;
//        foreach (var item in collection) {
//            if (++i > pageSize) break;
//            if (i > 1) sb.Append(",");
//            buildRow(item);
//        }
//        sb.Append("]");
//        sb.Append('}');
//    }
//    static void serializeRow(StringBuilder sb, KeyValuePair<string, PropertyType>[] properties, object[] values) {
//        sb.Append('{');
//        for (var c = 0; c < properties.Length; c++) {
//            sb.Append('"');
//            sb.Append(ToJson.DefaultCamelCasing(properties[c].Key));
//            sb.Append('"');
//            sb.Append(':');
//            var value = values[c];
//            ValueType(value, sb);
//            if (c + 1 < values.Length) sb.Append(',');
//        }
//        sb.Append('}');
//    }
//    static char[] charsToEscape = "\\\"\t\n\r".ToCharArray();
//    public static void ValueType(object value, StringBuilder sb) {
//        sb.Append(JsonSerializer.Serialize(value));
//        return;
//        if (value is null) {
//            sb.Append("null");
//        } else if (value is string s) {
//            if (s.IndexOfAny(charsToEscape) == -1) {
//                sb.Append('\"');
//                sb.Append(s);
//                sb.Append('\"');
//            } else {
//                sb.Append(JsonSerializer.Serialize(s));
//            }
//        } else if (value is int i) {
//            sb.Append(i.ToString());
//        } else if (value is double d) {
//            sb.Append(d.ToString());
//        } else if (value is Guid g) {
//            sb.Append(g);
//        } else if (value is DateTime dt) {
//            sb.Append(JsonSerializer.Serialize(dt));
//        } else if (value is float f) {
//            sb.Append(JsonSerializer.Serialize(f));
//        } else if (value is string[] sa) {
//            sb.Append(JsonSerializer.Serialize(sa));
//            //sb.Append('[');
//            //foreach (var str in sa) SerializeValueType(str, sb);
//            //sb.Append(']');
//        }
//    }
//    public static string DefaultCamelCasing(string s) {
//        return char.ToLower(s[0]) + s[1..];
//    }
//    class _customNamePolicy : JsonNamingPolicy {
//        public override string ConvertName(string name) => char.ToLower(name[0]) + name[1..];
//    }
//    static JsonSerializerOptions _defaultOptions = new JsonSerializerOptions() {
//        IncludeFields = true,
//        PropertyNamingPolicy = new _customNamePolicy()
//    };
//    public static string Pretty(string unPrettyJson) {
//        var options = new JsonSerializerOptions() { WriteIndented = true };
//        var jsonElement = JsonSerializer.Deserialize<JsonElement>(unPrettyJson);
//        return JsonSerializer.Serialize(jsonElement, options);
//    }
//    public static void FacetQueryResultData(FacetQueryResultData r, StringBuilder sb) {
//        sb.Append('{');
//        sb.Append("\"sourceCount\":");
//        sb.Append(r.SourceCount);
//        sb.Append(",\"durationMs\":");
//        ToJson.ValueType(r.DurationMs, sb);
//        sb.Append(",\"result\":");
//        r.Result.BuildJson(sb);
//        sb.Append(",\"facets\":[");
//        int i1 = 0;
//        foreach (var facet in r.Facets.Values) {
//            if (i1++ > 0) sb.Append(",");
//            sb.Append('{');
//            sb.Append("\"displayName\":");
//            sb.Append("\"" + facet.DisplayName + "\",");

//            if (facet.IsRangeFacet != null && facet.IsRangeFacet.Value) sb.Append("\"isRangeFacet\":true,");

//            if (facet.CodeName != null) {
//                sb.Append("\"codeName\":");
//                sb.Append("\"" + ToJson.DefaultCamelCasing(facet.CodeName) + "\",");
//            }
//            sb.Append("\"propertyId\":");
//            sb.Append("\"" + facet.PropertyId + "\",");
//            sb.Append("\"facets\":[");
//            int i2 = 0;
//            foreach (var facetValue in facet.Values) {
//                if (i2++ > 0) sb.Append(",");
//                sb.Append('{');
//                sb.Append("\"value\":");
//                ToJson.ValueType(facetValue.Value, sb);
//                if (facetValue.Value2 != null) {
//                    sb.Append(",\"value2\":");
//                    ToJson.ValueType(facetValue.Value, sb);
//                }
//                if (facetValue.Selected) sb.Append(",\"selected\":true");
//                sb.Append(",\"displayName\":");
//                ToJson.ValueType(facetValue.DisplayName, sb);

//                sb.Append(",\"count\":");
//                ToJson.ValueType(facetValue.Count, sb);

//                sb.Append('}');
//            }
//            sb.Append("]}");
//        }
//        sb.Append("]");
//        sb.Append('}');

//    }
//    public static void Generic(object v, StringBuilder output) {
//        if (v is IToJson t) {
//            t.BuildJson(output);
//        } else {
//            ValueType(v, output);
//        }
//    }
//    public static string Generic(object? v) {
//        if (v is null) return "null";
//        var sb = new StringBuilder();
//        Generic(v, sb);
//        return sb.ToString();
//    }
//}
