using System.Collections;
using GraphQLParser.AST;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Execution;

/// <summary>
/// Projects raw INodeData (with included relations) into JSON-shaped dictionaries,
/// honoring aliases, fragments, __typename and @skip/@include.
/// </summary>
internal static class Projector {

    public static object? ProjectNode(ExecutionContext ctx, INodeData node, IEnumerable<GraphQLSelectionSet> sets, List<object> path) {
        if (!ctx.Schema.TryGetObjectType(node.NodeType, out var runtime)) return null; // runtime type is not exposed in the schema
        var typeModel = ctx.Schema.Datamodel.NodeTypes[node.NodeType];
        var collected = DocumentWalker.CollectFields(ctx, name => GqlSchema.TypeConditionMatches(runtime, name), sets);
        var result = new Dictionary<string, object?>(collected.Count);
        foreach (var cf in collected) {
            var fieldName = cf.First.Name.StringValue;
            if (fieldName == "__typename") { result[cf.Key] = runtime.Name; continue; }
            if (!runtime.TryGetField(fieldName, out var field)) { result[cf.Key] = null; continue; }
            var fieldPath = new List<object>(path) { cf.Key };
            try {
                result[cf.Key] = projectField(ctx, node, typeModel, field, cf, fieldPath);
            } catch (GraphQLFieldException fe) {
                result[cf.Key] = null;
                ctx.AddError(fe.Message, cf.First, fieldPath);
            } catch (Exception ex) {
                result[cf.Key] = null;
                ctx.AddError("Field resolution failed: " + ex.Message, cf.First, fieldPath);
            }
        }
        return result;
    }

    static object? projectField(ExecutionContext ctx, INodeData node, NodeTypeModel typeModel, GqlField field, CollectedField cf, List<object> path) {
        switch (field.Source) {
            case FieldSource.Id: return node.Id.ToString();
            case FieldSource.DisplayName: {
                    if (!string.IsNullOrEmpty(node.DisplayName)) return node.DisplayName;
                    try { return typeModel.GetDisplayName(node); } catch { return null; }
                }
            case FieldSource.CreatedUtc: return Iso(node.CreatedUtc);
            case FieldSource.ChangedUtc: return Iso(node.ChangedUtc);
            case FieldSource.ScalarProperty: {
                    var p = field.Property!;
                    var value = node.TryGetValue(p.Id, out var raw) ? raw : safeDefault(p);
                    return ToJsonValue(value);
                }
            case FieldSource.EnumProperty: {
                    var p = field.Property!;
                    var value = node.TryGetValue(p.Id, out var raw) ? raw : safeDefault(p);
                    var intValue = value == null ? 0 : Convert.ToInt32(value);
                    var enumType = (GqlEnumType)field.Type.UnwrapNamed();
                    return enumType.TryGetByInt(intValue, out var ev) ? ev.Name : intValue.ToString();
                }
            case FieldSource.EnumArrayProperty: {
                    var p = field.Property!;
                    var value = node.TryGetValue(p.Id, out var raw) ? raw : null;
                    var enumType = (GqlEnumType)field.Type.UnwrapNamed();
                    var list = new List<object?>();
                    if (value is IEnumerable items and not string) {
                        foreach (var item in items) {
                            var intValue = Convert.ToInt32(item);
                            list.Add(enumType.TryGetByInt(intValue, out var ev) ? ev.Name : intValue.ToString());
                        }
                    }
                    return list;
                }
            case FieldSource.FileProperty: {
                    var p = field.Property!;
                    var value = node.TryGetValue(p.Id, out var raw) ? raw : null;
                    if (value is not FileValue file || file.IsEmpty) return null;
                    return projectFile(ctx, file, field, cf);
                }
            case FieldSource.RelationOne: {
                    node.Relations.TryGetOneRelation(field.Property!.Id, out var related);
                    return related == null ? null : ProjectNode(ctx, related, cf.SelectionSets, path);
                }
            case FieldSource.ReferenceOne: {
                    node.Relations.TryGetReference(field.Property!.Id, out var reference);
                    return reference == null ? null : ProjectNode(ctx, reference, cf.SelectionSets, path);
                }
            case FieldSource.RelationMany:
            case FieldSource.ReferenceMany: {
                    NodeDataWithRelations[]? related;
                    if (field.Source == FieldSource.RelationMany) node.Relations.TryGetManyRelation(field.Property!.Id, out related);
                    else node.Relations.TryGetReferences(field.Property!.Id, out related);
                    if (related == null) return new List<object?>();
                    // an aliased occurrence may cap smaller than the merged include, so apply this field's own top
                    var top = Arguments.GetInt(Arguments.Resolve(ctx, field, cf.First), "top");
                    IEnumerable<NodeDataWithRelations> items = related;
                    if (top is >= 0) items = items.Take(top.Value);
                    var list = new List<object?>();
                    var index = 0;
                    foreach (var item in items) {
                        list.Add(ProjectNode(ctx, item, cf.SelectionSets, [.. path, index]));
                        index++;
                    }
                    return list;
                }
            default:
                return null;
        }
    }

    static object projectFile(ExecutionContext ctx, FileValue file, GqlField field, CollectedField cf) {
        var fileType = (GqlObjectType)field.Type.UnwrapNamed();
        var collected = DocumentWalker.CollectFields(ctx, name => name == fileType.Name, cf.SelectionSets);
        var result = new Dictionary<string, object?>(collected.Count);
        foreach (var c in collected) {
            var name = c.First.Name.StringValue;
            if (name == "__typename") { result[c.Key] = fileType.Name; continue; }
            if (!fileType.TryGetField(name, out var fd)) { result[c.Key] = null; continue; }
            result[c.Key] = fd.Source switch {
                FieldSource.FileName => file.Name,
                FieldSource.FileSize => file.Size,
                FieldSource.FileWidth => file.Width,
                FieldSource.FileHeight => file.Height,
                FieldSource.FileContentType => safeContentType(file),
                _ => null,
            };
        }
        return result;
    }

    static string? safeContentType(FileValue file) {
        try { return file.ContentType; } catch { return null; }
    }

    static object? safeDefault(Datamodels.Properties.PropertyModel p) {
        try { return p.GetDefaultValue(); } catch { return null; }
    }

    public static string Iso(DateTime dt) {
        var utc = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
        return utc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Converts a stored property value into a JSON-serializable value.</summary>
    public static object? ToJsonValue(object? value) {
        switch (value) {
            case null: return null;
            case string or bool or int or long or double or float or decimal: return value;
            case DateTime dt: return Iso(dt);
            case DateTimeOffset dto: return dto.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            case Guid g: return g.ToString();
            case TimeSpan ts: return ts.ToString("c");
            case string[] strings: return strings.Cast<object?>().ToList();
            case Guid[] guids: return guids.Select(g => (object?)g.ToString()).ToList();
            case int[] ints: return ints.Cast<object?>().ToList();
            case IEnumerable items: {
                    var list = new List<object?>();
                    foreach (var item in items) list.Add(ToJsonValue(item));
                    return list;
                }
            default: return value.ToString();
        }
    }
}
