using System.Globalization;
using System.Text.Json;
using GraphQLParser.AST;
using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Execution;

/// <summary>Coerces the request's JSON variables against the operation's variable definitions.</summary>
internal static class VariableCoercer {

    public static Dictionary<string, object?> Coerce(ExecutionContext ctx, GraphQLOperationDefinition op, JsonElement? variablesJson) {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (op.Variables == null) return result;
        foreach (var def in op.Variables.Items) {
            var name = def.Variable.Name.StringValue;
            var type = ResolveTypeNode(ctx, def.Type);
            JsonElement provided = default;
            var hasValue = variablesJson.HasValue
                && variablesJson.Value.ValueKind == JsonValueKind.Object
                && variablesJson.Value.TryGetProperty(name, out provided);
            if (hasValue) {
                try {
                    result[name] = coerceJson(ctx, provided, type, name);
                } catch (GraphQLFieldException ex) {
                    throw ctx.RequestError($"Variable \"${name}\": {ex.Message}", def);
                }
            } else if (def.DefaultValue != null) {
                try {
                    result[name] = ValueResolver.Resolve(ctx, def.DefaultValue, type);
                } catch (GraphQLFieldException ex) {
                    throw ctx.RequestError($"Variable \"${name}\" default value: {ex.Message}", def);
                }
            } else if (type is GqlNonNullType) {
                throw ctx.RequestError($"Variable \"${name}\" of required type \"{type.ToTypeReference()}\" was not provided.", def);
            } else {
                result[name] = null;
            }
        }
        return result;
    }

    public static GqlType ResolveTypeNode(ExecutionContext ctx, GraphQLType typeNode) {
        switch (typeNode) {
            case GraphQLNonNullType nn: return new GqlNonNullType(ResolveTypeNode(ctx, nn.Type));
            case GraphQLListType list: return new GqlListType(ResolveTypeNode(ctx, list.Type));
            case GraphQLNamedType named: {
                    var name = named.Name.StringValue;
                    if (!ctx.Schema.TryGetType(name, out var type)) throw ctx.RequestError($"Unknown type \"{name}\".", typeNode);
                    return type;
                }
            default:
                throw ctx.RequestError("Unsupported type node.", typeNode);
        }
    }

    static object? coerceJson(ExecutionContext ctx, JsonElement json, GqlType type, string variableName) {
        if (type is GqlNonNullType nonNull) {
            if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) {
                throw new GraphQLFieldException($"null is not allowed for type \"{type.ToTypeReference()}\".");
            }
            return coerceJson(ctx, json, nonNull.OfType, variableName);
        }
        if (json.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (type is GqlListType listType) {
            var items = new List<object?>();
            if (json.ValueKind == JsonValueKind.Array) {
                foreach (var item in json.EnumerateArray()) items.Add(coerceJson(ctx, item, listType.OfType, variableName));
            } else {
                items.Add(coerceJson(ctx, json, listType.OfType, variableName));
            }
            return items;
        }
        switch (type) {
            case GqlScalarType scalar: return coerceScalar(json, scalar);
            case GqlEnumType enumType: {
                    if (json.ValueKind != JsonValueKind.String) throw new GraphQLFieldException($"expected an enum name of \"{enumType.Name}\".");
                    var name = json.GetString()!;
                    if (!enumType.TryGetByName(name, out var value)) throw new GraphQLFieldException($"\"{name}\" is not a value of enum \"{enumType.Name}\".");
                    return value;
                }
            case GqlInputObjectType inputType: {
                    if (json.ValueKind != JsonValueKind.Object) throw new GraphQLFieldException($"expected an object of input type \"{inputType.Name}\".");
                    var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var prop in json.EnumerateObject()) {
                        if (!inputType.TryGetInputField(prop.Name, out var fieldDef)) {
                            throw new GraphQLFieldException($"unknown field \"{prop.Name}\" on input type \"{inputType.Name}\".");
                        }
                        dict[prop.Name] = coerceJson(ctx, prop.Value, fieldDef.Type, variableName);
                    }
                    return dict;
                }
            default:
                throw new GraphQLFieldException($"type \"{type.ToTypeReference()}\" cannot be used as an input type.");
        }
    }

    static object coerceScalar(JsonElement json, GqlScalarType scalar) {
        try {
            switch (scalar.Name) {
                case "Int": return json.GetInt32();
                case "Long": return json.GetInt64();
                case "Float": return json.GetDouble();
                case "Decimal": return json.GetDecimal();
                case "Boolean":
                    if (json.ValueKind is JsonValueKind.True or JsonValueKind.False) return json.GetBoolean();
                    break;
                case "String":
                    if (json.ValueKind == JsonValueKind.String) return json.GetString()!;
                    break;
                case "ID":
                    if (json.ValueKind == JsonValueKind.String) return json.GetString()!;
                    if (json.ValueKind == JsonValueKind.Number) return json.GetRawText();
                    break;
                case "DateTime":
                    if (json.ValueKind == JsonValueKind.String) return ValueResolver.ParseDateTime(json.GetString()!);
                    break;
                default:
                    if (json.ValueKind == JsonValueKind.String) return json.GetString()!;
                    return json.GetRawText();
            }
        } catch (GraphQLFieldException) {
            throw;
        } catch (Exception ex) {
            throw new GraphQLFieldException($"invalid value for scalar \"{scalar.Name}\": {ex.Message}");
        }
        throw new GraphQLFieldException($"invalid value for scalar \"{scalar.Name}\" (got {json.ValueKind.ToString().ToLowerInvariant()}).");
    }
}
