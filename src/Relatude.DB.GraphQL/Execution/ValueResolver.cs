using System.Globalization;
using GraphQLParser.AST;
using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Execution;

/// <summary>
/// Coerces GraphQL AST value nodes into resolved CLR values:
/// Int→int, Float→double, Long→long, Decimal→decimal, String→string, Boolean→bool, ID→string,
/// DateTime→DateTime (UTC), enum→<see cref="GqlEnumValue"/>, lists→List&lt;object?&gt;, input objects→Dictionary&lt;string,object?&gt;.
/// </summary>
internal static class ValueResolver {

    public static object? Resolve(ExecutionContext ctx, GraphQLValue value, GqlType expectedType) {
        if (value is GraphQLVariable variable) {
            ctx.Variables.TryGetValue(variable.Name.StringValue, out var varValue);
            if (varValue == null && expectedType is GqlNonNullType) {
                throw new GraphQLFieldException($"Variable \"${variable.Name.StringValue}\" of a non-null type has no value.");
            }
            return varValue; // already coerced by VariableCoercer
        }
        if (expectedType is GqlNonNullType nonNull) {
            if (value is GraphQLNullValue) throw new GraphQLFieldException($"Null passed where type \"{expectedType.ToTypeReference()}\" is expected.");
            return Resolve(ctx, value, nonNull.OfType);
        }
        if (value is GraphQLNullValue) return null;
        if (expectedType is GqlListType listType) {
            if (value is GraphQLListValue listValue) {
                var items = new List<object?>();
                foreach (var item in listValue.Values ?? []) items.Add(Resolve(ctx, item, listType.OfType));
                return items;
            }
            return new List<object?> { Resolve(ctx, value, listType.OfType) }; // single value list coercion
        }
        switch (expectedType) {
            case GqlScalarType scalar: return resolveScalar(value, scalar);
            case GqlEnumType enumType: {
                    if (value is not GraphQLEnumValue ev) throw new GraphQLFieldException($"Expected an enum value of type \"{enumType.Name}\".");
                    if (!enumType.TryGetByName(ev.Name.StringValue, out var enumValue)) {
                        throw new GraphQLFieldException($"\"{ev.Name.StringValue}\" is not a value of enum \"{enumType.Name}\".");
                    }
                    return enumValue;
                }
            case GqlInputObjectType inputType: {
                    if (value is not GraphQLObjectValue ov) throw new GraphQLFieldException($"Expected an input object of type \"{inputType.Name}\".");
                    var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var field in ov.Fields ?? []) {
                        var name = field.Name.StringValue;
                        if (!inputType.TryGetInputField(name, out var fieldDef)) {
                            throw new GraphQLFieldException($"Unknown field \"{name}\" on input type \"{inputType.Name}\".");
                        }
                        dict[name] = Resolve(ctx, field.Value, fieldDef.Type);
                    }
                    return dict;
                }
            default:
                throw new GraphQLFieldException($"Cannot use type \"{expectedType.ToTypeReference()}\" as an input type.");
        }
    }

    static object? resolveScalar(GraphQLValue value, GqlScalarType scalar) {
        try {
            switch (scalar.Name) {
                case "Int":
                    if (value is GraphQLIntValue iv) return int.Parse(iv.Value.ToString(), CultureInfo.InvariantCulture);
                    break;
                case "Long":
                    if (value is GraphQLIntValue lv) return long.Parse(lv.Value.ToString(), CultureInfo.InvariantCulture);
                    break;
                case "Float":
                    if (value is GraphQLFloatValue fv) return double.Parse(fv.Value.ToString(), CultureInfo.InvariantCulture);
                    if (value is GraphQLIntValue fiv) return double.Parse(fiv.Value.ToString(), CultureInfo.InvariantCulture);
                    break;
                case "Decimal":
                    if (value is GraphQLFloatValue dv) return decimal.Parse(dv.Value.ToString(), CultureInfo.InvariantCulture);
                    if (value is GraphQLIntValue div) return decimal.Parse(div.Value.ToString(), CultureInfo.InvariantCulture);
                    break;
                case "String":
                    if (value is GraphQLStringValue sv) return sv.Value.ToString();
                    break;
                case "ID":
                    if (value is GraphQLStringValue idv) return idv.Value.ToString();
                    if (value is GraphQLIntValue idi) return idi.Value.ToString();
                    break;
                case "Boolean":
                    if (value is GraphQLBooleanValue bv) return bv.BoolValue;
                    break;
                case "DateTime":
                    if (value is GraphQLStringValue dtv) return ParseDateTime(dtv.Value.ToString());
                    break;
                default:
                    // unknown custom scalar: pass the raw literal text through
                    if (value is GraphQLStringValue anyString) return anyString.Value.ToString();
                    break;
            }
        } catch (GraphQLFieldException) {
            throw;
        } catch (Exception ex) {
            throw new GraphQLFieldException($"Invalid value for scalar \"{scalar.Name}\": {ex.Message}");
        }
        throw new GraphQLFieldException($"Invalid literal for scalar \"{scalar.Name}\".");
    }

    public static DateTime ParseDateTime(string text) {
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)) {
            throw new GraphQLFieldException($"\"{text}\" is not a valid ISO-8601 DateTime.");
        }
        return dt;
    }
}
