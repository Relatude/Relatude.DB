using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Introspection;

/// <summary>
/// The full __schema introspection result materialized once as a plain object tree
/// (dictionaries tagged with "__typename", lists, scalars). Named type references point to the
/// full type dictionaries, so arbitrarily deep introspection selections resolve correctly;
/// the cycles this creates are harmless because projection follows the (finite) selection set.
/// </summary>
internal sealed class IntrospectionData {
    public required Dictionary<string, object?> SchemaData { get; init; }
    public required Dictionary<string, Dictionary<string, object?>> TypesByName { get; init; }

    public static IntrospectionData Build(GqlSchema schema) {
        var types = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var t in schema.Types.Values) types[t.Name] = newTypeDict();
        foreach (var name in _metaTypeNames) types[name] = newTypeDict();

        Dictionary<string, object?> byName(string name) => types[name];
        Dictionary<string, object?> typeRef(GqlType t) => t switch {
            GqlNonNullType nn => wrapperRef("NON_NULL", typeRef(nn.OfType)),
            GqlListType list => wrapperRef("LIST", typeRef(list.OfType)),
            GqlNamedType named => byName(named.Name),
            _ => throw new InvalidOperationException(),
        };
        Dictionary<string, object?> nonNull(string name) => wrapperRef("NON_NULL", byName(name));
        Dictionary<string, object?> listOfNonNull(string name) => wrapperRef("LIST", nonNull(name));

        foreach (var t in schema.Types.Values) fillType(types[t.Name], t, typeRef);
        fillMetaTypes(types, byName, nonNull, listOfNonNull);

        var schemaData = new Dictionary<string, object?> {
            ["__typename"] = "__Schema",
            ["description"] = "GraphQL schema generated from the Relatude.DB datamodel.",
            ["queryType"] = byName(schema.QueryType.Name),
            ["mutationType"] = null,
            ["subscriptionType"] = null,
            ["types"] = types.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (object?)kv.Value).ToList(),
            ["directives"] = buildDirectives(byName, nonNull),
        };
        return new IntrospectionData { SchemaData = schemaData, TypesByName = types };
    }

    static Dictionary<string, object?> newTypeDict() => new(StringComparer.Ordinal) { ["__typename"] = "__Type" };

    static Dictionary<string, object?> wrapperRef(string kind, Dictionary<string, object?> ofType) => new(StringComparer.Ordinal) {
        ["__typename"] = "__Type", ["kind"] = kind, ["name"] = null, ["ofType"] = ofType,
    };

    static void fillType(Dictionary<string, object?> dict, GqlNamedType t, Func<GqlType, Dictionary<string, object?>> typeRef) {
        dict["kind"] = t switch {
            GqlScalarType => "SCALAR",
            GqlObjectType => "OBJECT",
            GqlInterfaceType => "INTERFACE",
            GqlEnumType => "ENUM",
            GqlInputObjectType => "INPUT_OBJECT",
            _ => "SCALAR",
        };
        dict["name"] = t.Name;
        dict["description"] = t.Description;
        dict["specifiedByURL"] = null;
        dict["ofType"] = null;
        dict["fields"] = t is IGqlCompositeType composite
            ? composite.Fields.Select(f => (object?)fieldDict(f, typeRef)).ToList()
            : null;
        dict["interfaces"] = t switch {
            GqlObjectType o => o.Interfaces.Select(i => (object?)typeRef(i)).ToList(),
            GqlInterfaceType i => i.Interfaces.Select(x => (object?)typeRef(x)).ToList(),
            _ => null,
        };
        dict["possibleTypes"] = t is GqlInterfaceType iface
            ? iface.PossibleTypes.Select(p => (object?)typeRef(p)).ToList()
            : null;
        dict["enumValues"] = t is GqlEnumType e
            ? e.Values.Select(v => (object?)new Dictionary<string, object?>(StringComparer.Ordinal) {
                ["__typename"] = "__EnumValue", ["name"] = v.Name, ["description"] = null,
                ["isDeprecated"] = false, ["deprecationReason"] = null,
            }).ToList()
            : null;
        dict["inputFields"] = t is GqlInputObjectType input
            ? input.InputFields.Select(f => (object?)inputValueDict(f.Name, f.Description, typeRef(f.Type), null)).ToList()
            : null;
        dict["isOneOf"] = t is GqlInputObjectType ? false : null;
    }

    static Dictionary<string, object?> fieldDict(GqlField f, Func<GqlType, Dictionary<string, object?>> typeRef) => new(StringComparer.Ordinal) {
        ["__typename"] = "__Field",
        ["name"] = f.Name,
        ["description"] = f.Description,
        ["args"] = f.Arguments.Select(a => (object?)inputValueDict(a.Name, a.Description, typeRef(a.Type), a.HasDefaultValue ? formatLiteral(a.DefaultValue) : null)).ToList(),
        ["type"] = typeRef(f.Type),
        ["isDeprecated"] = false,
        ["deprecationReason"] = null,
    };

    static Dictionary<string, object?> inputValueDict(string name, string? description, Dictionary<string, object?> type, string? defaultValue) => new(StringComparer.Ordinal) {
        ["__typename"] = "__InputValue",
        ["name"] = name,
        ["description"] = description,
        ["type"] = type,
        ["defaultValue"] = defaultValue,
        ["isDeprecated"] = false,
        ["deprecationReason"] = null,
    };

    static string formatLiteral(object? value) => value switch {
        null => "null",
        bool b => b ? "true" : "false",
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
    };

    static List<object?> buildDirectives(Func<string, Dictionary<string, object?>> byName, Func<string, Dictionary<string, object?>> nonNull) {
        Dictionary<string, object?> directive(string name, string description, string[] locations, List<object?> args) => new(StringComparer.Ordinal) {
            ["__typename"] = "__Directive",
            ["name"] = name,
            ["description"] = description,
            ["locations"] = locations.Cast<object?>().ToList(),
            ["args"] = args,
            ["isRepeatable"] = false,
        };
        return [
            directive("skip", "Skips this field or fragment when the if argument is true.",
                ["FIELD", "FRAGMENT_SPREAD", "INLINE_FRAGMENT"],
                [inputValueDict("if", null, nonNull("Boolean"), null)]),
            directive("include", "Includes this field or fragment only when the if argument is true.",
                ["FIELD", "FRAGMENT_SPREAD", "INLINE_FRAGMENT"],
                [inputValueDict("if", null, nonNull("Boolean"), null)]),
            directive("deprecated", "Marks a schema element as no longer supported.",
                ["FIELD_DEFINITION", "ARGUMENT_DEFINITION", "INPUT_FIELD_DEFINITION", "ENUM_VALUE"],
                [inputValueDict("reason", null, byName("String"), "\"No longer supported\"")]),
        ];
    }

    static readonly string[] _metaTypeNames = ["__Schema", "__Type", "__Field", "__InputValue", "__EnumValue", "__Directive", "__TypeKind", "__DirectiveLocation"];

    static void fillMetaTypes(
        Dictionary<string, Dictionary<string, object?>> types,
        Func<string, Dictionary<string, object?>> byName,
        Func<string, Dictionary<string, object?>> nonNull,
        Func<string, Dictionary<string, object?>> listOfNonNull) {

        var includeDeprecatedArg = (Func<List<object?>>)(() => [inputValueDict("includeDeprecated", null, byName("Boolean"), "false")]);
        Dictionary<string, object?> field(string name, Dictionary<string, object?> type, List<object?>? args = null) => new(StringComparer.Ordinal) {
            ["__typename"] = "__Field", ["name"] = name, ["description"] = null,
            ["args"] = args ?? [], ["type"] = type, ["isDeprecated"] = false, ["deprecationReason"] = null,
        };
        void fillObject(string name, params Dictionary<string, object?>[] fields) {
            var d = types[name];
            d["kind"] = "OBJECT"; d["name"] = name; d["description"] = null; d["specifiedByURL"] = null;
            d["fields"] = fields.Cast<object?>().ToList();
            d["interfaces"] = new List<object?>();
            d["possibleTypes"] = null; d["enumValues"] = null; d["inputFields"] = null; d["ofType"] = null; d["isOneOf"] = null;
        }
        void fillEnum(string name, params string[] values) {
            var d = types[name];
            d["kind"] = "ENUM"; d["name"] = name; d["description"] = null; d["specifiedByURL"] = null;
            d["fields"] = null; d["interfaces"] = null; d["possibleTypes"] = null;
            d["enumValues"] = values.Select(v => (object?)new Dictionary<string, object?>(StringComparer.Ordinal) {
                ["__typename"] = "__EnumValue", ["name"] = v, ["description"] = null, ["isDeprecated"] = false, ["deprecationReason"] = null,
            }).ToList();
            d["inputFields"] = null; d["ofType"] = null; d["isOneOf"] = null;
        }

        fillObject("__Schema",
            field("description", byName("String")),
            field("types", wrapperRef("NON_NULL", listOfNonNull("__Type"))),
            field("queryType", nonNull("__Type")),
            field("mutationType", byName("__Type")),
            field("subscriptionType", byName("__Type")),
            field("directives", wrapperRef("NON_NULL", listOfNonNull("__Directive"))));
        fillObject("__Type",
            field("kind", nonNull("__TypeKind")),
            field("name", byName("String")),
            field("description", byName("String")),
            field("specifiedByURL", byName("String")),
            field("fields", listOfNonNull("__Field"), includeDeprecatedArg()),
            field("interfaces", listOfNonNull("__Type")),
            field("possibleTypes", listOfNonNull("__Type")),
            field("enumValues", listOfNonNull("__EnumValue"), includeDeprecatedArg()),
            field("inputFields", listOfNonNull("__InputValue"), includeDeprecatedArg()),
            field("ofType", byName("__Type")),
            field("isOneOf", byName("Boolean")));
        fillObject("__Field",
            field("name", nonNull("String")),
            field("description", byName("String")),
            field("args", wrapperRef("NON_NULL", listOfNonNull("__InputValue")), includeDeprecatedArg()),
            field("type", nonNull("__Type")),
            field("isDeprecated", nonNull("Boolean")),
            field("deprecationReason", byName("String")));
        fillObject("__InputValue",
            field("name", nonNull("String")),
            field("description", byName("String")),
            field("type", nonNull("__Type")),
            field("defaultValue", byName("String")),
            field("isDeprecated", nonNull("Boolean")),
            field("deprecationReason", byName("String")));
        fillObject("__EnumValue",
            field("name", nonNull("String")),
            field("description", byName("String")),
            field("isDeprecated", nonNull("Boolean")),
            field("deprecationReason", byName("String")));
        fillObject("__Directive",
            field("name", nonNull("String")),
            field("description", byName("String")),
            field("locations", wrapperRef("NON_NULL", listOfNonNull("__DirectiveLocation"))),
            field("args", wrapperRef("NON_NULL", listOfNonNull("__InputValue")), includeDeprecatedArg()),
            field("isRepeatable", nonNull("Boolean")));
        fillEnum("__TypeKind", "SCALAR", "OBJECT", "INTERFACE", "UNION", "ENUM", "INPUT_OBJECT", "LIST", "NON_NULL");
        fillEnum("__DirectiveLocation",
            "QUERY", "MUTATION", "SUBSCRIPTION", "FIELD", "FRAGMENT_DEFINITION", "FRAGMENT_SPREAD", "INLINE_FRAGMENT",
            "VARIABLE_DEFINITION", "SCHEMA", "SCALAR", "OBJECT", "FIELD_DEFINITION", "ARGUMENT_DEFINITION",
            "INTERFACE", "UNION", "ENUM", "ENUM_VALUE", "INPUT_OBJECT", "INPUT_FIELD_DEFINITION");
    }
}
