using Relatude.DB.Datamodels;
using System.Globalization;
namespace Relatude.DB.Query.Parsing.Tokens;
public enum ParsedTypes {
    FromParameter,
    Null,
    Boolean,
    String,
    LongString, // stored as string to delay parsing when needed type is known ( int vs decimal, etc)
    IntString, // stored as string to delay parsing when needed type is known ( int vs decimal, etc)
    FloatString, // stored as string to delay parsing when needed type is known ( int vs decimal, etc)
    ArrayOfStrings,
    ArrayOfNumberStrings,
    ArrayOfNone,
}
public class ValueConstantToken : TokenBase {
    readonly object? _value;
    public ParsedTypes ParsedTypeHint { get; }
    public object? DirectValue => _value;
    public ValueConstantToken(object? value, ParsedTypes parsedType, string code, int pos1, int pos2) : base(code, pos1, pos2) {
        _value = value;
        ParsedTypeHint = parsedType;
    }

    public Guid[] GetNodeTypeGuids(Datamodel dm) {
        if (_value is Guid[] ids) return ids;
        var strArray = cast<string[]>();
        var guids = new Guid[strArray.Length];
        for (int n = 0; n < strArray.Length; n++) {
            var v = strArray[n];
            if (Guid.TryParse(v, out var g)) {
                guids[n] = g;
            } else if (dm.NodeTypesByShortName.TryGetValue(v, out var types)) {
                if (types.Length == 1) {
                    guids[n] = types[0].Id;
                } else {
                    throw new Exception("Ambiguous nodetype name '" + v + "' found when trying to parse node type guids. ");
                }
            } else if (dm.NodeTypesByFullName.TryGetValue(v, out var type)) {
                guids[n] = type.Id;
            } else {
                throw new Exception("Cannot locate a valid node type guid from value '" + v + "'. ");
            }
        }
        return guids;
    }
    public Guid GetPropertyId(Datamodel dm) {
        return dm.GetPropertyGuid(GetStringValue());
    }
    public Guid[] GetGuids() {
        if (_value is Guid[] ids) return ids;
        var strArray = cast<string[]>();
        var guids = new Guid[strArray.Length];
        for (int n = 0; n < strArray.Length; n++) {
            var v = strArray[n];
            if (Guid.TryParse(v, out var g)) guids[n] = g;
            else throw new Exception("Cannot locate a valid guid from value '" + v + "'. ");
        }
        return guids;
    }
    public object[] GetPropertyValues(Datamodel dm, Guid propertyId) {
        var property = dm.Properties[propertyId];
        if (_value == null) return [];
        if (_value is not string[] strArray) throw new Exception("Stored value is not string array. ");
        object[] values = new object[strArray.Length];
        for (int n = 0; n < strArray.Length; n++) {
            var v = strArray[n];
            if (property.TryParse(v, out var pv)) {
                values[n] = pv;
            } else {
                throw new Exception($"Unable to parse value '{v}' for property '{property.CodeName}'");
            }
        }
        return values;
    }
    public string GetStringValue() => cast<string>();
    public double? GetDoubleOrNullValue() => _value == null ? null : cast<double>();
    public float? GetFloatOrNullValue() => _value == null ? null : cast<float>();
    public bool? GetBoolOrNullValue() => _value == null ? null : cast<bool>();
    public bool GetBoolValue() => cast<bool>();
    public int? GetIntOrNullValue() => _value == null ? null : cast<int>();
    public int GetIntValue() => cast<int>();
    public long? GetLongOrNullValue() => _value == null ? null : cast<long>();
    public long GetLongValue() => cast<long>();
    public double GetDoubleValue() => cast<double>();
    public string[] GetArrayOfStrings() => cast<string[]>();
    T cast<T>() {
        if (_value == null) throw new Exception("Stored value is null. ");
        switch (ParsedTypeHint) {
            case ParsedTypes.FromParameter: return (T)_value; // must match directly
            case ParsedTypes.Boolean: return (T)_value;
            case ParsedTypes.String: return (T)_value;
            case ParsedTypes.IntString: {
                    if (_value is not string strValue) throw new Exception("Stored value is not number string. "); // should never happen, internal error
                    object v = typeof(T) switch {
                        var t when t == typeof(int) => int.Parse(strValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
                        var t when t == typeof(long) => long.Parse(strValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
                        var t when t == typeof(double) => double.Parse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        var t when t == typeof(byte) => byte.Parse(strValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
                        var t when t == typeof(decimal) => decimal.Parse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        var t when t == typeof(float) => float.Parse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        _ => throw new Exception("Cannot cast number string to type " + typeof(T) + ". "),
                    };
                    return (T)v;
                }
            case ParsedTypes.LongString: {
                    if (_value is not string longStrValue) throw new Exception("Stored value is not long number string. "); // should never happen, internal error
                    object v = typeof(T) switch {
                        var t when t == typeof(long) => long.Parse(longStrValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
                        var t when t == typeof(double) => double.Parse(longStrValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        var t when t == typeof(decimal) => decimal.Parse(longStrValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        _ => throw new Exception("Cannot cast long number string to type " + typeof(T) + ". "),
                    };
                    return (T)v;
                }
            case ParsedTypes.FloatString: {
                    if (_value is not string strValue) throw new Exception("Stored value is not float number string. "); // should never happen, internal error
                    object v = typeof(T) switch {
                        var t when t == typeof(double) => double.Parse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        var t when t == typeof(decimal) => decimal.Parse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        var t when t == typeof(float) => float.Parse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        _ => throw new Exception("Cannot cast float number string to type " + typeof(T) + ". "),
                    };
                    return (T)v;
                }
            case ParsedTypes.ArrayOfStrings: {
                    if (typeof(T) != typeof(string[])) throw new Exception("Cannot cast string to type " + typeof(T) + ". ");
                    if (_value is not string[]) throw new Exception("Stored value is not string array. "); // should never happen, internal error
                    return (T)_value;
                }
            case ParsedTypes.ArrayOfNumberStrings: {
                    if (typeof(T) != typeof(int[])) throw new Exception("Cannot cast number string array to type " + typeof(T) + ". ");
                    if (_value is not string[]) throw new Exception("Stored value is not number string array. "); // should never happen, internal error
                    var arr = ((string[])_value!).Select(s => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray();
                    return (T)(object)arr;
                }
            case ParsedTypes.ArrayOfNone: {
                    if (typeof(T) == typeof(string[])) return (T)(object)Array.Empty<string>();
                    if (typeof(T) == typeof(int[])) return (T)(object)Array.Empty<int>();
                    throw new Exception("Cannot cast empty array to type " + typeof(T) + ". ");
                }
            default:
                throw new Exception("Unknown parsed type " + ParsedTypeHint + ". ");
        }
    }

    public override TokenTypes TokenType => TokenTypes.ValueConstant;
    static public ValueConstantToken Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {

        // limited types supported when parsing values:
        // null, boolean, string, integer, float

        pos = SkipWhiteSpace(code, pos);
        var firstChar = code[pos];
        string strValue;
        if (firstChar == '\"' || firstChar == '\'') {
            strValue = StringLiteralParser.extractStringLiteral(code, pos, firstChar, out newPos);
            return new ValueConstantToken(strValue, ParsedTypes.String, code, pos, newPos);
        }

        // is null:
        if (isNextElement(firstChar, code, pos, "null")) {
            newPos = pos + 4;
            return new(null, ParsedTypes.Null, code, pos, newPos);
        }

        // is boolean:
        if (isNextElement(firstChar, code, pos, "true") || isNextElement(firstChar, code, pos, "True")) {
            newPos = pos + 4;
            return new(true, ParsedTypes.Boolean, code, pos, newPos);
        }
        if (isNextElement(firstChar, code, pos, "false") || isNextElement(firstChar, code, pos, "False")) {
            newPos = pos + 5;
            return new(false, ParsedTypes.Boolean, code, pos, newPos);
        }

        // array:
        if (firstChar == '[') {
            pos++;
            pos = SkipWhiteSpace(code, pos);
            if (code[pos] == ']') {
                // empty array:
                newPos = pos + 1;
                return new(Array.Empty<object>(), ParsedTypes.ArrayOfNone, code, pos, newPos);
            }
            var isStringArray = code[pos] == '\"' || code[pos] == '\'';
            if (isStringArray) {
                List<string> strValues = [];
                while (true) {
                    pos = SkipWhiteSpace(code, pos);
                    var sv = StringLiteralParser.extractStringLiteral(code, pos, code[pos], out var afterStringPos);
                    strValues.Add(sv);
                    pos = SkipWhiteSpace(code, afterStringPos);
                    var nextChar = code[pos];
                    if (nextChar == ',') {
                        pos++;
                        continue;
                    } else if (nextChar == ']') {
                        newPos = pos + 1;
                        return new(strValues.ToArray(), ParsedTypes.ArrayOfStrings, code, pos, newPos);
                    } else {
                        throw new SyntaxException("Array of strings is not properly formatted. ", code, pos);
                    }
                }
            } else {
                // assumes array of numbers:
                var endBracketPos = code.IndexOf(']', pos);
                if (endBracketPos == -1) throw new SyntaxException("Array constant not closed. ", code, pos);
                var arrayContent = code[pos..endBracketPos];
                var strValues = arrayContent.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                newPos = endBracketPos + 1;
                return new(strValues, ParsedTypes.ArrayOfNumberStrings, code, pos, newPos);
            }
        }

        // must be number:
        var startPos = pos;
        var hasDecimalPoint = false;
        var boolIsNegative = false;
        while (pos < code.Length) {
            if (char.IsNumber(code[pos])) {
                pos++;
            } else if (code[pos] == '.') {
                if (hasDecimalPoint) throw new SyntaxException("Invalid number format. ", code, pos);
                hasDecimalPoint = true;
                pos++;
            } else if (code[pos] == '-' && pos == startPos) {
                pos++;
                boolIsNegative = true;
            } else {
                break;
            }
        }
        strValue = code[startPos..pos];
        newPos = pos;
        ParsedTypes numberType;
        if (hasDecimalPoint) {
            numberType = ParsedTypes.FloatString;
        } else {
            if (strValue.Length > 10 + (boolIsNegative ? 1 : 0)) {
                numberType = ParsedTypes.LongString;
            } else {
                numberType = ParsedTypes.IntString;
            }
        }
        return new(strValue, numberType, code, startPos, newPos);

    }
    public override string ToString() {
        return string.Empty + _value?.ToString();
    }
}
