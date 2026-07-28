using System.Globalization;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.GraphQL.Schema;
using Relatude.DB.Query;

namespace Relatude.DB.GraphQL.Execution;

/// <summary>Collects query parameters (P0, P1, ...) referenced from generated query text.</summary>
internal sealed class ParameterBag {
    public List<Parameter> Parameters { get; } = [];
    /// <summary>Adds a parameter and returns its name. User values never end up in query text directly.</summary>
    public string Add(object? value) {
        var name = "P" + Parameters.Count;
        Parameters.Add(new Parameter(name, value));
        return name;
    }
}

/// <summary>
/// Translates a resolved filter input value (nested dictionaries) into a parameterized
/// Relatude query lambda body over the variable "n".
/// </summary>
internal static class FilterTranslator {

    /// <summary>Returns the predicate body (without "n => "), or null when the filter adds no condition.</summary>
    public static string? Translate(Dictionary<string, object?> filter, GqlInputObjectType inputType, ParameterBag parameters) {
        var parts = new List<string>();
        foreach (var (key, value) in filter) {
            if (value == null) continue;
            if (!inputType.TryGetInputField(key, out var field)) throw new GraphQLFieldException($"Unknown filter field \"{key}\".");
            switch (field.Op) {
                case FilterOp.And: {
                        foreach (var item in (List<object?>)value) {
                            if (item is not Dictionary<string, object?> dict) continue;
                            var p = Translate(dict, inputType, parameters);
                            if (p != null) parts.Add("(" + p + ")");
                        }
                        break;
                    }
                case FilterOp.Or: {
                        var terms = new List<string>();
                        foreach (var item in (List<object?>)value) {
                            if (item is not Dictionary<string, object?> dict) continue;
                            var p = Translate(dict, inputType, parameters);
                            terms.Add(p == null ? "(1 == 1)" : "(" + p + ")"); // an empty branch matches everything
                        }
                        if (terms.Count > 0) parts.Add("(" + string.Join(" || ", terms) + ")");
                        break;
                    }
                case FilterOp.Not: {
                        if (value is Dictionary<string, object?> dict) {
                            var p = Translate(dict, inputType, parameters);
                            if (p != null) parts.Add("!(" + p + ")");
                        }
                        break;
                    }
                default: {
                        var prop = field.Property ?? throw new GraphQLFieldException($"Filter field \"{key}\" is not translatable.");
                        if (value is not Dictionary<string, object?> ops) throw new GraphQLFieldException($"Filter field \"{key}\" expects an object value.");
                        var opInput = (GqlInputObjectType)field.Type.UnwrapNamed();
                        foreach (var (opKey, opValue) in ops) {
                            if (!opInput.TryGetInputField(opKey, out var opField)) throw new GraphQLFieldException($"Unknown filter operator \"{opKey}\".");
                            var term = leaf(prop, opField.Op, opValue, parameters);
                            if (term != null) parts.Add(term);
                        }
                        break;
                    }
            }
        }
        return parts.Count == 0 ? null : string.Join(" && ", parts);
    }

    static string? leaf(PropertyModel prop, FilterOp op, object? value, ParameterBag parameters) {
        var member = "n." + prop.CodeName;
        switch (op) {
            case FilterOp.Eq: return $"{member} == {parameters.Add(convert(prop, value))}";
            case FilterOp.Ne: return $"{member} != {parameters.Add(convert(prop, value))}";
            case FilterOp.Gt: return $"{member} > {parameters.Add(convert(prop, value))}";
            case FilterOp.Gte: return $"{member} >= {parameters.Add(convert(prop, value))}";
            case FilterOp.Lt: return $"{member} < {parameters.Add(convert(prop, value))}";
            case FilterOp.Lte: return $"{member} <= {parameters.Add(convert(prop, value))}";
            case FilterOp.In: {
                    var items = asList(value);
                    if (items.Count == 0) return "(1 == 0)"; // in [] matches nothing
                    var terms = items.Select(v => $"{member} == {parameters.Add(convert(prop, v))}");
                    return "(" + string.Join(" || ", terms) + ")";
                }
            case FilterOp.Nin: {
                    var items = asList(value);
                    if (items.Count == 0) return null; // nin [] excludes nothing
                    var terms = items.Select(v => $"{member} == {parameters.Add(convert(prop, v))}");
                    return "!(" + string.Join(" || ", terms) + ")";
                }
            case FilterOp.RelEq:
                return relatedTerm(prop, value, parameters);
            case FilterOp.RelIn: {
                    var items = asList(value);
                    if (items.Count == 0) return "(1 == 0)";
                    var terms = items.Select(v => relatedTerm(prop, v, parameters));
                    return "(" + string.Join(" || ", terms) + ")";
                }
            default:
                return null;
        }
    }

    static string relatedTerm(PropertyModel prop, object? value, ParameterBag parameters) {
        var guid = parseGuid(value);
        if (prop is RelationPropertyModel rp) {
            var method = rp.IsMany ? "Has" : "Is";
            return $"n.{prop.CodeName}.{method}({parameters.Add(guid)})";
        }
        // Reference properties store the related node's Guid directly on the node.
        // The row evaluator has no Guid==Guid support, but mixed Guid/string operands
        // are compared as strings — so the parameter is passed as a (normalized) string.
        return $"n.{prop.CodeName} == {parameters.Add(guid.ToString())}";
    }

    static List<object?> asList(object? value) => value as List<object?> ?? (value == null ? [] : [value]);

    static Guid parseGuid(object? value) {
        if (value is string s && Guid.TryParse(s, out var g)) return g;
        throw new GraphQLFieldException($"\"{value}\" is not a valid node id.");
    }

    /// <summary>Converts a resolved input value into the CLR type of the property, for use as a query parameter.</summary>
    internal static object? convert(PropertyModel prop, object? value) {
        if (value == null) return null;
        if (value is GqlEnumValue ev) value = ev.IntValue;
        try {
            return prop.PropertyType switch {
                PropertyType.Boolean => (bool)value,
                PropertyType.Integer => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                PropertyType.String => (string)value,
                PropertyType.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                PropertyType.Float => Convert.ToSingle(value, CultureInfo.InvariantCulture),
                PropertyType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                PropertyType.Long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                PropertyType.DateTime => (DateTime)value,
                PropertyType.DateTimeOffset => new DateTimeOffset(((DateTime)value).ToUniversalTime(), TimeSpan.Zero),
                // as a string: the row evaluator compares mixed Guid/string operands as strings (it has no Guid support)
                PropertyType.Guid => Guid.Parse((string)value).ToString(),
                _ => value,
            };
        } catch (Exception) {
            throw new GraphQLFieldException($"Invalid filter value for \"{prop.CodeName}\".");
        }
    }
}
