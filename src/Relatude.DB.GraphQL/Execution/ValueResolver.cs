using System.Globalization;
using Relatude.DB.GraphQL.Language;
using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Execution;

/// <summary>
/// Coerces GraphQL AST value nodes into resolved CLR values:
/// Int→int, Float→double, Long→long, Decimal→decimal, String→string, Boolean→bool, ID→string,
/// DateTime→DateTime (UTC), enum→<see cref="GqlEnumValue"/>, lists→List&lt;object?&gt;, input objects→Dictionary&lt;string,object?&gt;.
/// </summary>
internal static class ValueResolver {

    public static object? Resolve(ExecutionContext ctx, ValueNode value, GqlType expectedType) {
        if (value is VariableValue variable) {
            ctx.Variables.TryGetValue(variable.Name, out var varValue);
            if (varValue == null && expectedType is GqlNonNullType) {
                throw new GraphQLFieldException($"Variable \"${variable.Name}\" of a non-null type has no value.");
            }
            return varValue; // already coerced by VariableCoercer
        }
        if (expectedType is GqlNonNullType nonNull) {
            if (value is NullValue) throw new GraphQLFieldException($"Null passed where type \"{expectedType.ToTypeReference()}\" is expected.");
            return Resolve(ctx, value, nonNull.OfType);
        }
        if (value is NullValue) return null;
        if (expectedType is GqlListType listType) {
            if (value is ListValue listValue) {
                var items = new List<object?>();
                foreach (var item in listValue.Values) items.Add(Resolve(ctx, item, listType.OfType));
                return items;
            }
            return new List<object?> { Resolve(ctx, value, listType.OfType) }; // single value list coercion
        }
        switch (expectedType) {
            case GqlScalarType scalar: return resolveScalar(value, scalar);
            case GqlEnumType enumType: {
                    if (value is not EnumValue ev) throw new GraphQLFieldException($"Expected an enum value of type \"{enumType.Name}\".");
                    if (!enumType.TryGetByName(ev.Name, out var enumValue)) {
                        throw new GraphQLFieldException($"\"{ev.Name}\" is not a value of enum \"{enumType.Name}\".");
                    }
                    return enumValue;
                }
            case GqlInputObjectType inputType: {
                    if (value is not ObjectValue ov) throw new GraphQLFieldException($"Expected an input object of type \"{inputType.Name}\".");
                    var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var field in ov.Fields) {
                        if (!inputType.TryGetInputField(field.Name, out var fieldDef)) {
                            throw new GraphQLFieldException($"Unknown field \"{field.Name}\" on input type \"{inputType.Name}\".");
                        }
                        dict[field.Name] = Resolve(ctx, field.Value, fieldDef.Type);
                    }
                    return dict;
                }
            default:
                throw new GraphQLFieldException($"Cannot use type \"{expectedType.ToTypeReference()}\" as an input type.");
        }
    }

    static object? resolveScalar(ValueNode value, GqlScalarType scalar) {
        try {
            switch (scalar.Name) {
                case "Int":
                    if (value is IntValue iv) return int.Parse(iv.Text, CultureInfo.InvariantCulture);
                    break;
                case "Long":
                    if (value is IntValue lv) return long.Parse(lv.Text, CultureInfo.InvariantCulture);
                    break;
                case "Float":
                    if (value is FloatValue fv) return double.Parse(fv.Text, CultureInfo.InvariantCulture);
                    if (value is IntValue fiv) return double.Parse(fiv.Text, CultureInfo.InvariantCulture);
                    break;
                case "Decimal":
                    if (value is FloatValue dv) return decimal.Parse(dv.Text, CultureInfo.InvariantCulture);
                    if (value is IntValue div) return decimal.Parse(div.Text, CultureInfo.InvariantCulture);
                    break;
                case "String":
                    if (value is StringValue sv) return sv.Value;
                    break;
                case "ID":
                    if (value is StringValue idv) return idv.Value;
                    if (value is IntValue idi) return idi.Text;
                    break;
                case "Boolean":
                    if (value is BooleanValue bv) return bv.Value;
                    break;
                case "DateTime":
                    if (value is StringValue dtv) return ParseDateTime(dtv.Value);
                    break;
                default:
                    // unknown custom scalar: pass the raw literal text through
                    if (value is StringValue anyString) return anyString.Value;
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
