using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Endpoints;

/// <summary>
/// The schema as the admin UI's explorer reads it: every named type with its fields, arguments, input fields and
/// enum values, plus what each type and field stands for in the datamodel (a node type, a result page, a filter,
/// a relation...). Built from the schema itself, so it works whether the endpoint allows introspection or not.
/// Type references are in SDL notation, such as "[Article!]!".
/// </summary>
public static class SchemaDescription {
    public sealed record ArgumentInfo(string Name, string Type, string? Description, string? DefaultValue);

    /// <param name="Kind">What the field is: system, property, enum, file, geo, relation, reference, single, list, view,
    /// create, update, delete, items, count, value.</param>
    /// <param name="Many">True for list fields whose items are nodes (relations, references, items).</param>
    public sealed record FieldInfo(string Name, string Type, string? Description, string Kind, bool Many, string? Property, string? NodeType, List<ArgumentInfo> Args);

    public sealed record InputFieldInfo(string Name, string Type, string? Description, string? Property);

    /// <param name="Kind">SCALAR, OBJECT, INTERFACE, ENUM or INPUT_OBJECT, as introspection names them.</param>
    /// <param name="Role">root, node, result, object, filter, operator, input, orderBy, enum or scalar.</param>
    public sealed record TypeInfo(
        string Name, string Kind, string Role, string? Description, string? NodeType,
        List<string>? Interfaces, List<string>? PossibleTypes,
        List<FieldInfo>? Fields, List<InputFieldInfo>? InputFields, List<string>? EnumValues);

    public sealed record Info(string QueryType, string? MutationType, string? Description, List<TypeInfo> Types);

    public static Info Describe(GqlSchema schema) {
        var types = schema.Types.Values.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => describe(schema, t)).ToList();
        return new Info(schema.QueryType.Name, schema.MutationType?.Name, schema.Definition.Description, types);
    }

    static TypeInfo describe(GqlSchema schema, GqlNamedType t) {
        switch (t) {
            case GqlObjectType o:
                return new TypeInfo(o.Name, "OBJECT", roleOf(schema, o), o.Description, o.NodeType?.FullName,
                    o.Interfaces.Select(i => i.Name).ToList(), null, o.Fields.Select(field).ToList(), null, null);
            case GqlInterfaceType i:
                return new TypeInfo(i.Name, "INTERFACE", "node", i.Description, i.NodeType?.FullName,
                    i.Interfaces.Select(x => x.Name).ToList(), i.PossibleTypes.Select(p => p.Name).ToList(), i.Fields.Select(field).ToList(), null, null);
            case GqlEnumType e:
                return new TypeInfo(e.Name, "ENUM", e.Values.Any(v => v.Property != null) ? "orderBy" : "enum", e.Description, null,
                    null, null, null, null, e.Values.Select(v => v.Name).ToList());
            case GqlInputObjectType io:
                return new TypeInfo(io.Name, "INPUT_OBJECT", inputRole(io), io.Description, null, null, null, null,
                    io.InputFields.Select(f => new InputFieldInfo(f.Name, f.Type.ToTypeReference(), f.Description, f.Property?.CodeName)).ToList(), null);
            default:
                return new TypeInfo(t.Name, "SCALAR", "scalar", t.Description, null, null, null, null, null, null);
        }
    }

    static string roleOf(GqlSchema schema, GqlObjectType o) {
        if (o == schema.QueryType || o == schema.MutationType) return "root";
        if (o.NodeType != null) return "node";
        if (o.Fields.Any(f => f.Source == FieldSource.WrapperItems)) return "result";
        return "object";
    }

    static string inputRole(GqlInputObjectType io) {
        if (io.InputFields.Any(f => f.Op is FilterOp.And or FilterOp.Or or FilterOp.Not)) return "filter";
        if (io.InputFields.Any(f => f.Op != FilterOp.None)) return "operator";
        return "input";
    }

    static FieldInfo field(GqlField f) {
        var (kind, many) = f.Source switch {
            FieldSource.Id or FieldSource.DisplayName or FieldSource.CreatedUtc or FieldSource.ChangedUtc => ("system", false),
            FieldSource.ScalarProperty => ("property", false),
            FieldSource.EnumProperty or FieldSource.EnumArrayProperty => ("enum", false),
            FieldSource.FileProperty => ("file", false),
            FieldSource.GeoProperty => ("geo", false),
            FieldSource.RelationOne => ("relation", false),
            FieldSource.RelationMany => ("relation", true),
            FieldSource.ReferenceOne => ("reference", false),
            FieldSource.ReferenceMany => ("reference", true),
            FieldSource.RootSingle => ("single", false),
            FieldSource.RootList => ("list", false),
            FieldSource.RootView => ("view", false),
            FieldSource.MutationCreate => ("create", false),
            FieldSource.MutationUpdate => ("update", false),
            FieldSource.MutationDelete => ("delete", false),
            FieldSource.WrapperItems => ("items", true),
            FieldSource.WrapperTotalCount or FieldSource.WrapperPageIndex or FieldSource.WrapperPageSize or FieldSource.WrapperExecutionTimeMs => ("count", false),
            _ => ("value", false),
        };
        var description = f.Description;
        if (f.Source == FieldSource.RootView && !string.IsNullOrWhiteSpace(f.ViewQuery)) description = (description == null ? "" : description + " ") + "Query: " + f.ViewQuery;
        return new FieldInfo(f.Name, f.Type.ToTypeReference(), description, kind, many, f.Property?.CodeName, f.TargetNodeType?.FullName,
            f.Arguments.Select(a => new ArgumentInfo(a.Name, a.Type.ToTypeReference(), a.Description, a.HasDefaultValue ? literal(a.DefaultValue) : null)).ToList());
    }

    static string? literal(object? value) => value switch {
        null => "null",
        bool b => b ? "true" : "false",
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        IFormattable n => n.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
