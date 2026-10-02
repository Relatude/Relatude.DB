using System.Globalization;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.GraphQL.Schema;
using Relatude.DB.Transactions;

namespace Relatude.DB.GraphQL.Execution;

/// <summary>
/// Runs the create/update/delete root fields. Each field is one transaction; the created or updated node
/// is read back afterwards and projected like a query result.
/// </summary>
internal static class MutationExecutor {

    public static object? Execute(ExecutionContext ctx, GqlField field, CollectedField cf, List<object> path) {
        var args = Arguments.Resolve(ctx, field, cf.First);
        var type = field.TargetNodeType!;
        return field.Source switch {
            FieldSource.MutationCreate => create(ctx, field, cf, type, args, path),
            FieldSource.MutationUpdate => update(ctx, field, cf, type, args, path),
            FieldSource.MutationDelete => delete(ctx, type, args),
            _ => throw new GraphQLFieldException($"\"{field.Name}\" is not a mutation."),
        };
    }

    static Dictionary<string, object?> inputOf(GqlField field, Dictionary<string, object?> args)
        => args.TryGetValue("input", out var v) && v is Dictionary<string, object?> d ? d : throw new GraphQLFieldException("The input argument is required.");

    static GqlInputObjectType inputTypeOf(GqlField field) => (GqlInputObjectType)field.GetArgument("input")!.Type.UnwrapNamed();

    static Guid idOf(Dictionary<string, object?> args) {
        var text = Arguments.GetString(args, "id");
        if (text == null || !Guid.TryParse(text, out var id) || id == Guid.Empty) throw new GraphQLFieldException($"\"{text}\" is not a valid node id.");
        return id;
    }

    static object? create(ExecutionContext ctx, GqlField field, CollectedField cf, NodeTypeModel type, Dictionary<string, object?> args, List<object> path) {
        var input = inputOf(field, args);
        var inputType = inputTypeOf(field);
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        var values = new Properties<object>(type.AllProperties.Count);
        var relations = new List<(RelationPropertyModel Property, List<Guid> Ids)>();
        foreach (var (key, value) in input) {
            if (!inputType.TryGetInputField(key, out var f) || f.Property == null) continue;
            if (f.Source is FieldSource.RelationOne or FieldSource.RelationMany) {
                relations.Add(((RelationPropertyModel)f.Property, idList(f.Name, value)));
                continue;
            }
            var stored = ToStoreValue(f, value);
            if (stored != null) values.Add(f.Property.Id, stored);
        }
        // the store does not fill defaults on insert: give every unset property the type's default, as the editor does
        foreach (var p in type.AllProperties.Values) {
            if (p.Internal || values.ContainsKey(p.Id)) continue;
            if (p.PropertyType is PropertyType.Relation or PropertyType.Embedded or PropertyType.File) continue;
            if (NodeConstants.IsNodeDateProperty(p.Id)) continue;
            object? d;
            try { d = type.GetDefaultValue(p); } catch { continue; }
            if (d != null) values.Add(p.Id, d);
        }
        var transaction = new TransactionData();
        transaction.InsertOrFail(new NodeData(id, 0, type.Id, now, now, values, null), null, null);
        foreach (var (p, ids) in relations) {
            // Set rather than Add: a related node that may only have one partner (a child with a parent) is moved over
            foreach (var related in ids) transaction.SetRelation(p.RelationId, source(id, p, related), target(id, p, related));
        }
        run(ctx, transaction);
        return QueryExecutor.FetchAndProjectNode(ctx, type, id, (GqlNamedType)field.Type.UnwrapNamed(), cf.SelectionSets, path);
    }

    static object? update(ExecutionContext ctx, GqlField field, CollectedField cf, NodeTypeModel type, Dictionary<string, object?> args, List<object> path) {
        var id = idOf(args);
        var input = inputOf(field, args);
        var inputType = inputTypeOf(field);
        if (!exists(ctx, type, id)) throw new GraphQLFieldException($"No {type.CodeName} with id {id} exists.");
        var transaction = new TransactionData();
        foreach (var (key, value) in input) {
            if (!inputType.TryGetInputField(key, out var f) || f.Property == null) continue;
            switch (f.Source) {
                case FieldSource.RelationOne: {
                        var p = (RelationPropertyModel)f.Property;
                        var ids = idList(f.Name, value);
                        if (ids.Count == 0) clearRelations(transaction, id, p);
                        else transaction.SetRelation(p.RelationId, source(id, p, ids[0]), target(id, p, ids[0]));
                        break;
                    }
                case FieldSource.RelationMany: {
                        var p = (RelationPropertyModel)f.Property;
                        clearRelations(transaction, id, p);
                        foreach (var related in idList(f.Name, value)) transaction.SetRelation(p.RelationId, source(id, p, related), target(id, p, related));
                        break;
                    }
                default: {
                        // null clears the field: the type's default is written, since the store keeps the old value when a property is only removed
                        var stored = ToStoreValue(f, value) ?? defaultOf(type, f.Property);
                        if (stored == null) transaction.ResetProperty(id, f.Property.Id);
                        else transaction.UpdateIfDifferentProperty(id, f.Property.Id, stored);
                        break;
                    }
            }
        }
        if (transaction.Actions.Count > 0) run(ctx, transaction);
        return QueryExecutor.FetchAndProjectNode(ctx, type, id, (GqlNamedType)field.Type.UnwrapNamed(), cf.SelectionSets, path);
    }

    static object delete(ExecutionContext ctx, NodeTypeModel type, Dictionary<string, object?> args) {
        var id = idOf(args);
        if (!exists(ctx, type, id)) return false;
        var transaction = new TransactionData();
        transaction.DeleteOrFail(id);
        run(ctx, transaction);
        return true;
    }

    static object? defaultOf(NodeTypeModel type, PropertyModel p) {
        try { return type.GetDefaultValue(p); } catch { return null; }
    }

    static bool exists(ExecutionContext ctx, NodeTypeModel type, Guid id) {
        var parameters = new ParameterBag();
        var collection = QueryExecutor.RunQuery(ctx, $"{type.CodeName}.WhereInIds({parameters.Add(new[] { id })})", parameters);
        return collection.NodeValues.Any();
    }

    static void run(ExecutionContext ctx, TransactionData transaction) {
        try {
            ctx.Host.RunTransaction(transaction, ctx.QueryContext);
        } catch (GraphQLFieldException) {
            throw;
        } catch (Exception ex) {
            throw new GraphQLFieldException("The change was rejected: " + ex.Message);
        }
    }

    // a relation property names the type's own side; the relation itself is stored source → target
    static Guid source(Guid self, RelationPropertyModel p, Guid related) => p.FromTargetToSource ? related : self;
    static Guid target(Guid self, RelationPropertyModel p, Guid related) => p.FromTargetToSource ? self : related;

    static void clearRelations(TransactionData transaction, Guid self, RelationPropertyModel p) {
        if (p.FromTargetToSource) transaction.ClearRelationsWithTarget(p.RelationId, self);
        else transaction.ClearRelationsWithSource(p.RelationId, self);
    }

    static List<Guid> idList(string fieldName, object? value) {
        var list = new List<Guid>();
        switch (value) {
            case null: break;
            case string s: list.Add(parseId(fieldName, s)); break;
            case List<object?> items:
                foreach (var item in items) if (item != null) list.Add(parseId(fieldName, item));
                break;
            default: list.Add(parseId(fieldName, value)); break;
        }
        return list;
    }

    static Guid parseId(string fieldName, object value) {
        if (value is string s && Guid.TryParse(s, out var g) && g != Guid.Empty) return g;
        throw new GraphQLFieldException($"{fieldName}: \"{value}\" is not a valid node id.");
    }

    /// <summary>Converts a resolved input value into the CLR value the store keeps for the property; null clears it.</summary>
    internal static object? ToStoreValue(GqlInputField f, object? value) {
        if (value == null) return null;
        var p = f.Property!;
        try {
            switch (f.Source) {
                case FieldSource.EnumProperty: return value is GqlEnumValue ev ? ev.IntValue : Convert.ToInt32(value, CultureInfo.InvariantCulture);
                case FieldSource.EnumArrayProperty: return asList(value).Select(v => v is GqlEnumValue e ? e.IntValue : Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToArray();
                case FieldSource.GeoProperty: {
                        var d = (Dictionary<string, object?>)value;
                        return new GeoCoordinate(Convert.ToDouble(d["latitude"], CultureInfo.InvariantCulture), Convert.ToDouble(d["longitude"], CultureInfo.InvariantCulture));
                    }
                case FieldSource.ReferenceOne: return parseId(f.Name, value);
                case FieldSource.ReferenceMany: return idList(f.Name, value).ToArray();
            }
            return p.PropertyType switch {
                PropertyType.Boolean => (bool)value,
                PropertyType.Integer => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                PropertyType.String => (string)value,
                PropertyType.StringArray => asList(value).Select(v => (string)v!).ToArray(),
                PropertyType.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                PropertyType.Float => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                PropertyType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                PropertyType.Long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                PropertyType.DateTime => DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc),
                PropertyType.DateTimeOffset => new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc)),
                PropertyType.TimeSpan => TimeSpan.Parse((string)value, CultureInfo.InvariantCulture),
                PropertyType.Guid => Guid.Parse((string)value),
                PropertyType.GuidArray => asList(value).Select(v => Guid.Parse((string)v!)).ToArray(),
                PropertyType.EnumArray => asList(value).Select(v => Convert.ToInt32(v, CultureInfo.InvariantCulture)).ToArray(),
                _ => throw new GraphQLFieldException($"{f.Name}: the property type {p.PropertyType} cannot be written through this endpoint."),
            };
        } catch (GraphQLFieldException) {
            throw;
        } catch (Exception ex) {
            throw new GraphQLFieldException($"{f.Name}: invalid value ({ex.Message}).");
        }
    }

    static List<object?> asList(object? value) => value as List<object?> ?? (value == null ? [] : [value]);
}
