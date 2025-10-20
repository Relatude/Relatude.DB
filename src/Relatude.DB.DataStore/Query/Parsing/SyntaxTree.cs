using Relatude.DB.Datamodels;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Intrinsics.Arm;
using System.Text;
namespace Relatude.DB.Query.Parsing;
/// <summary>
/// This is the first step in parsing a query
/// It will parse the string and return a tree of primitive syntax units like:
/// Values, Methods, Lamdas Exp, Anonymous constructions etc.
/// It is focused on parsing the query. It is comparable to a tokenizer in a normal database query parser.
/// </summary>
public abstract class SyntaxTree {
    static public SyntaxUnit Parse(string code, IEnumerable<Parameter> parameters) {
        if (code == null) throw new ArgumentNullException("Query code is null. ");
        code = code.Trim();
        if (code.Length == 0) throw new Exception("Query code is empty. ");
        var unit = SyntaxUnit.ParseStatement(code, 0, out _, null, parameters);
        if (unit == null) throw new Exception("The result of the query parsing is empty. ");
        return unit;
    }
}
public enum SyntaxUnitTypes {
    Empty,
    Variable,
    ValueConstant,
    ExpressionBracket,
    MethodCall,
    LambdaDeclaration,
    AnonymousObject,
    ObjectConstruction,
    OperatorExpression,
    PreFixOperatorExpression,
}
public class SyntaxException : Exception {
    string _code;
    int _pos;
    string? _message;
    public SyntaxException(string code, int pos) {
        _pos = pos;
        _code = code;
    }
    public SyntaxException(string message, string code, int pos) {
        _pos = pos;
        _code = code;
        _message = message;
    }
    string extract(int from, int to, string text) {
        if (from < 0) from = 0;
        if (to > text.Length) to = text.Length;
        return text[from..to];
    }
    public override string Message {
        get {
            var padding = 30;
            if (_pos <= 0) _pos = 0;
            else if (_pos >= _code.Length) _pos = _code.Length - 1;
            return (_message == null ? "" : _message)
                + "Unexpected syntax at postion " + _pos + ": "
                + (_pos - padding > 0 ? "..." : "")
                + extract(_pos - padding, _pos, _code)
                //+ _code[_pos] + "\u0333"
                + " ==> " + _code[_pos] + " <== "
                + extract(_pos + 1, _pos + padding, _code)
                + (_pos + padding < _code.Length ? "..." : "");
        }
    }
}
public abstract class SyntaxUnit {
    public string Code { get; }
    public int Pos1 { get; }
    public int Pos2 { get; set; }
    protected SyntaxUnit(string code, int pos1, int pos2) {
        Pos1 = pos1;
        Pos1 = pos2;
        Code = code;
    }
    public static SyntaxUnit? ParseStatement(string code, int pos, out int newPos, SyntaxUnit? prevUnit, IEnumerable<Parameter> parameters) {
        do {
            pos = SkipWhiteSpace(code, pos);
            var nextUnit = ParseUnit(code, pos, out pos, prevUnit, parameters);
            newPos = pos;
            if (nextUnit == null) return prevUnit;
            prevUnit = nextUnit;
        } while (pos < code.Length);
        return prevUnit;
    }
    public static SyntaxUnit? ParseUnit(string code, int pos, out int newPos, SyntaxUnit? prevUnit, IEnumerable<Parameter> parameters) {
        pos = SkipWhiteSpace(code, pos);
        var unitType = whatIsNext(code, pos, prevUnit);
        newPos = pos;
        return unitType switch {
            SyntaxUnitTypes.Variable => VariableReferenceSyntax.Parse(code, pos, out newPos, parameters),
            SyntaxUnitTypes.MethodCall => MethodCallSyntax.Parse(code, pos, out newPos, prevUnit, parameters),
            SyntaxUnitTypes.ExpressionBracket => BracketSyntax.Parse(code, pos, out newPos, parameters),
            SyntaxUnitTypes.OperatorExpression => OperatorExpressionSyntax.Parse(code, pos, out newPos, prevUnit, parameters),
            SyntaxUnitTypes.PreFixOperatorExpression => PreFixSyntax.Parse(code, pos, out newPos, parameters),
            SyntaxUnitTypes.LambdaDeclaration => LambdaSyntax.Parse(code, pos, out newPos, parameters),
            SyntaxUnitTypes.ObjectConstruction => ObjectConstructionSyntax.Parse(code, pos, out newPos, parameters),
            SyntaxUnitTypes.AnonymousObject => AnonymousObjectSyntax.Parse(code, pos, out newPos, parameters),
            SyntaxUnitTypes.ValueConstant => ValueConstantSyntax.Parse(code, pos, out newPos, parameters),
            SyntaxUnitTypes.Empty => null,
            _ => throw new SyntaxException(code, pos),
        };
    }
    internal static bool isNextElement(char firstNoneWhiteSpaceChar, string code, int pos, string word) {
        if (word == "false") {
            int aa = 1;
        }
        if (firstNoneWhiteSpaceChar != word[0]) return false; // first char does not match
        if (pos + word.Length > code.Length) return false; // not enough code left for word
        if (code[pos..(pos + word.Length)] == word) return true; // match
        if (pos + word.Length == code.Length) return true; // last expression, so ok
        return false;
        //var c = code[pos + word.Length]; // next char must be whitespace, operator or end bracket
        //return char.IsWhiteSpace(c) || c == ')' || OperatorExpressionSyntax.IsOperatorChar(c);
    }
    protected static SyntaxUnitTypes whatIsNext(string code, int pos, SyntaxUnit? prevUnit) {
        pos = SkipWhiteSpace(code, pos);
        var firstNoneWhiteSpaceChar = code[pos];
        if (char.IsDigit(firstNoneWhiteSpaceChar) || firstNoneWhiteSpaceChar == '\"') return SyntaxUnitTypes.ValueConstant;
        if (isNextElement(firstNoneWhiteSpaceChar, code, pos, "false")) return SyntaxUnitTypes.ValueConstant;
        if (isNextElement(firstNoneWhiteSpaceChar, code, pos, "true")) return SyntaxUnitTypes.ValueConstant;
        if (isNextElement(firstNoneWhiteSpaceChar, code, pos, "null")) return SyntaxUnitTypes.ValueConstant;
        if (firstNoneWhiteSpaceChar == '[') return SyntaxUnitTypes.ValueConstant;
        if (firstNoneWhiteSpaceChar == '(') {
            var pos2 = code.IndexOf(')', pos); // posFirstEndBracket 
            if (pos2 > -1) {
                pos2 = SkipWhiteSpace(code, pos2 + 1); // in case: (...) =>
                if (code.Length - pos2 > 2 && code[pos2..(pos2 + 2)] == "=>") return SyntaxUnitTypes.LambdaDeclaration;
            }
            return SyntaxUnitTypes.ExpressionBracket;
        }
        if (firstNoneWhiteSpaceChar == ')') return SyntaxUnitTypes.Empty;
        if (OperatorExpressionSyntax.IsOperatorChar(firstNoneWhiteSpaceChar)) {
            if (prevUnit == null) {
                if (firstNoneWhiteSpaceChar == '-' && whatIsNext(code, pos + 1, prevUnit) == SyntaxUnitTypes.ValueConstant) {
                    // minus infront of number is a constant
                    return SyntaxUnitTypes.ValueConstant;
                }
                return SyntaxUnitTypes.PreFixOperatorExpression;
            } else {
                return SyntaxUnitTypes.OperatorExpression;
            }
        }
        var startFirstWord = pos;
        pos = SkipUntilAfterReferenceIncludingGenerics(code, pos);
        if (pos == startFirstWord)
            return SyntaxUnitTypes.Empty;
        if (pos + 1 < code.Length && code[startFirstWord..(pos + 1)] == "new ") {
            pos = SkipWhiteSpace(code, pos);
            if (code[pos] == '{') return SyntaxUnitTypes.AnonymousObject;
            else return SyntaxUnitTypes.ObjectConstruction;
        }
        pos = SkipWhiteSpace(code, pos);
        if (code.Length - pos > 2 && code[pos..(pos + 2)] == "=>") return SyntaxUnitTypes.LambdaDeclaration;
        if (pos < code.Length && code[pos] == '(') return SyntaxUnitTypes.MethodCall;
        return SyntaxUnitTypes.Variable;
    }
    protected static int SkipWhiteSpace(string code, int pos) {
        while (pos < code.Length && char.IsWhiteSpace(code[pos])) pos++;
        return pos;
    }
    protected static int SkipUntilAfterReferenceIncludingGenerics(string code, int pos) {
        while (pos < code.Length) {
            var c = code[pos];
            if (char.IsLetterOrDigit(c) || c == '_') {
                pos++;
            } else if (c == '.') {
                pos++;
            } else if (c == '<') {
                var endGenerics = code.IndexOf('>', pos);
                if (endGenerics != -1 && isValidAsGenericArgument(code[pos..endGenerics])) {
                    pos = endGenerics + 1;
                } else {
                    // in this case it will assume < is an operator and not start of generic argument
                }
                return pos;
            } else {
                return pos;
            }
        }
        return pos;
    }
    protected static bool isValidAsGenericArgument(string v) {
        if (v.Length == 0) return false; // must have length
        if (!(char.IsLetter(v[0]) || v[0] == '_')) return false; // must start with _ or letter
        foreach (var c in v) if (!char.IsLetterOrDigit(c) && c != '_' && c != '.' && c != ',' && c != ' ') return false; // can only contain letter or digit or _ or . or , or space
        return true;
    }
    public override string ToString() {
        return string.Empty;// "[" + GetType().Name.Substring(0, 2) + "]";
    }
    public abstract SyntaxUnitTypes SyntaxType { get; }
}
public enum ParameterTypes {
    Boolean,
    Integer,
    Double,
    Byte,
    Decimal,
    Long,
    Float,
    String,
    StringArray,
    IntegerArray,
    NodeTypeGuids,
}
public enum ParsedTypes {
    NotParsed,
    Null,
    Boolean,
    String,
    LongNumberString, // stored as string to delay parsing when needed type is known ( int vs decimal, etc)
    IntegerNumberString, // stored as string to delay parsing when needed type is known ( int vs decimal, etc)
    FloatingNumberString, // stored as string to delay parsing when needed type is known ( int vs decimal, etc)
    ArrayOfStrings,
    ArrayOfNumberStrings,
}
public class ValueConstantSyntax : SyntaxUnit {
    readonly object? _value;
    public ParsedTypes ParsedType { get; }
    public object? DirectValue => _value;
    public ValueConstantSyntax(object? value, ParsedTypes parsedType, string code, int pos1, int pos2) : base(code, pos1, pos2) {
        _value = value;
        ParsedType = parsedType;
    }
    public Guid[] GetNodeTypeGuids(Datamodel dm) {
        if (_value is Guid[] ids) return ids;
        var strArray = getValueOfType(ParameterTypes.StringArray) as string[];
        if (strArray == null) throw new Exception("Stored value is not string array. ");
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
            }
            throw new Exception("Cannot locate a valid node type guid from value '" + v + "'. ");
        }
        return guids;
    }
    public Guid GetPropertyId(Datamodel dm) {
        return dm.GetPropertyGuid(GetStringValue());
    }
    public Guid[] GetGuids() {
        if (_value is Guid[] ids) return ids;
        var strArray = getValueOfType(ParameterTypes.StringArray) as string[];
        if (strArray == null) throw new Exception("Stored value is not string array. ");
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
        //    valueString = valueString.Trim();
        //    if (valueString.StartsWith('[') && valueString.EndsWith(']')) valueString = valueString[1..^1];
        //    _propertyGuid = propertyId; // name, guid or id
        //    var property = _dm.Properties[_propertyGuid];
        //    foreach (var value in stringValues(valueString)) {
        //        if (property.TryParse(value, out var v)) {
        //            _values.Add(v);
        //        } else {
        //            throw new Exception($"Unable to parse value '{value}' for property '{property.CodeName}'");
        //        }
        //    }
        //}
        //IEnumerable<string> stringValues(string s) {
        //    // parse values, and remove quotes thay may surround them. using yield return
        //    var i = 0;
        //    while (i < s.Length) {
        //        var start = i;
        //        while (i < s.Length && s[i] != ',') i++;
        //        var value = s[start..i].Trim();
        //        if (value.StartsWith('\'') && value.EndsWith('\'')) value = value[1..^1];
        //        if (value.StartsWith('\"') && value.EndsWith('\"')) value = value[1..^1];
        //        yield return value;
        //        i++;
        //    }
        //}
    }
    public string GetStringValue() {
        var v = getValueOfType(ParameterTypes.String);
        if (v is not string strValue) throw new Exception("Stored value is not string. ");
        return strValue;
    }
    public double? GetDoubleOrNullValue() {
        if (_value == null) return null;
        return (double)getValueOfType(ParameterTypes.Double);
    }
    public float? GetFloatOrNullValue() {
        if (_value == null) return null;
        return (float)getValueOfType(ParameterTypes.Float);
    }
    public bool? GetBoolOrNullValue() {
        if (_value == null) return null;
        return (bool)getValueOfType(ParameterTypes.Boolean);
    }
    public bool GetBoolValue() {
        return (bool)getValueOfType(ParameterTypes.Boolean);
    }
    public int? GetIntOrNullValue() {
        if (_value == null) return null;
        return (int)getValueOfType(ParameterTypes.Integer);
    }
    public int GetIntValue() {
        return (int)getValueOfType(ParameterTypes.Integer);
    }
    public long? GetLongOrNullValue() {
        if (_value == null) return null;
        return (long)getValueOfType(ParameterTypes.Long);
    }
    public long GetLongValue() {
        return (long)getValueOfType(ParameterTypes.Long);
    }
    public double GetDoubleValue() {
        return (double)getValueOfType(ParameterTypes.Double);
    }
    public string[] GetArrayOfStrings() {
        var v = getValueOfType(ParameterTypes.StringArray);
        if (v is not string[] strValues) throw new Exception("Stored value is not string array. ");
        return strValues;
    }

    object getValueOfType(ParameterTypes wantedType) {
        if (_value == null) throw new Exception("Stored value is null. ");
        switch (ParsedType) {
            case ParsedTypes.NotParsed: return _value; // must match directly
            case ParsedTypes.Boolean: {
                    if (wantedType != ParameterTypes.Boolean) throw new Exception("Cannot cast boolean to type " + wantedType + ". ");
                    if (_value is not bool) throw new Exception("Stored value is not boolean. "); // should never happen, internal error
                    return _value;
                }
            case ParsedTypes.String: {
                    if (wantedType != ParameterTypes.String) throw new Exception("Cannot cast string to type " + wantedType + ". ");
                    if (_value is not string) throw new Exception("Stored value is not string. "); // should never happen, internal error
                    return _value;
                }
            case ParsedTypes.IntegerNumberString: {
                    if (_value is not string strValue) throw new Exception("Stored value is not number string. "); // should never happen, internal error
                    return wantedType switch {
                        ParameterTypes.Integer => int.Parse(strValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
                        ParameterTypes.Long => long.Parse(strValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
                        ParameterTypes.Double => double.Parse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        ParameterTypes.Byte => byte.Parse(strValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
                        ParameterTypes.Decimal => decimal.Parse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        ParameterTypes.Float => float.Parse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        _ => throw new Exception("Cannot cast number string to type " + wantedType + ". "),
                    };
                }
            case ParsedTypes.LongNumberString: {
                    if (_value is not string longStrValue) throw new Exception("Stored value is not long number string. "); // should never happen, internal error
                    return wantedType switch {
                        ParameterTypes.Long => long.Parse(longStrValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
                        ParameterTypes.Double => double.Parse(longStrValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        ParameterTypes.Decimal => decimal.Parse(longStrValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        _ => throw new Exception("Cannot cast long number string to type " + wantedType + ". "),
                    };
                }

            case ParsedTypes.FloatingNumberString: {
                    if (_value is not string strValue) throw new Exception("Stored value is not float number string. "); // should never happen, internal error
                    return wantedType switch {
                        ParameterTypes.Double => double.Parse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        ParameterTypes.Decimal => decimal.Parse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        ParameterTypes.Float => float.Parse(strValue, NumberStyles.Float, CultureInfo.InvariantCulture),
                        _ => throw new Exception("Cannot cast float number string to type " + wantedType + ". "),
                    };
                }
            case ParsedTypes.ArrayOfStrings: {
                    if (wantedType != ParameterTypes.StringArray) throw new Exception("Cannot cast string to type " + wantedType + ". ");
                    if (_value is not string[]) throw new Exception("Stored value is not string array. "); // should never happen, internal error
                    return _value;
                }
            case ParsedTypes.ArrayOfNumberStrings: {
                    if (wantedType != ParameterTypes.IntegerArray) throw new Exception("Cannot cast number string array to type " + wantedType + ". ");
                    if (_value is not string[]) throw new Exception("Stored value is not number string array. "); // should never happen, internal error
                    return ((string[])_value!).Select(s => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture)).ToArray();
                }
            default:
                throw new Exception("Unknown parsed type " + ParsedType + ". ");
        }
    }
    public override SyntaxUnitTypes SyntaxType => SyntaxUnitTypes.ValueConstant;
    static public ValueConstantSyntax Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {

        // limited types supported when parsing values:
        // null, boolean, string, integer, float

        pos = SkipWhiteSpace(code, pos);
        var firstChar = code[pos];
        string strValue;
        if (firstChar == '\"' || firstChar == '\'') {
            strValue = StringLiteralParser.extractStringLiteral(code, pos, firstChar, out newPos);
            return new ValueConstantSyntax(strValue, ParsedTypes.String, code, pos, newPos);
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
                var arrayContent = code[(pos)..endBracketPos];
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
            numberType = ParsedTypes.FloatingNumberString;
        } else {
            if (strValue.Length > 10 + (boolIsNegative ? 1 : 0)) {
                numberType = ParsedTypes.LongNumberString;
            } else {
                numberType = ParsedTypes.IntegerNumberString;
            }
        }
        return new(strValue, numberType, code, startPos, newPos);

    }
    public override string ToString() {
        return string.Empty + _value?.ToString();
    }
}
public class VariableReferenceSyntax : SyntaxUnit {
    public VariableReferenceSyntax(string name, string code, int pos1, int pos2) : base(code, pos1, pos2) {
        Name = name;
    }
    public string Name { get; }
    static public SyntaxUnit Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {
        var startPos = SkipWhiteSpace(code, pos);
        pos = SkipUntilAfterReferenceIncludingGenerics(code, pos);
        newPos = pos;
        pos = SkipWhiteSpace(code, pos);
        var name = code[startPos..pos].Trim();
        if (pos < code.Length) {
            var nextChar = code[pos];
            if (nextChar == '(') {
                // last part of name is method, so remove method name
                var lastDot = name.LastIndexOf('.');
                if (lastDot == -1) throw new SyntaxException("No object reference is found for method call. ", code, pos);
                name = name.Substring(0, lastDot);
                newPos = startPos + name.Length + 1; // subtract length of method and moving past last dot
            }
        }
        if (parameters != null) {
            // check if this is a parameter
            foreach (var p in parameters) {
                if (p.Name == name) {
                    return new ValueConstantSyntax(p.Value, ParsedTypes.NotParsed, code, startPos, newPos);
                }
            }
        }
        return new VariableReferenceSyntax(name, code, startPos, newPos);
    }
    public override SyntaxUnitTypes SyntaxType => SyntaxUnitTypes.Variable;
    public string MemberName => Name.Split('.').Last();
    public override string ToString() {
        return base.ToString() + Name;
    }
}
public class MethodCallSyntax : SyntaxUnit {
    public MethodCallSyntax(string code, int pos1, int pos2) : base(code, pos1, pos2) { }
    public SyntaxUnit? Subject { get; set; }
    public string Name { get; set; } = String.Empty;
    public List<string> GenericParams { get; set; } = new();
    public List<SyntaxUnit> Arguments { get; set; } = new();
    static public MethodCallSyntax Parse(string code, int pos, out int newPos, SyntaxUnit? subject, IEnumerable<Parameter> parameters) {
        //if (subject == null) throw new SyntaxException("Unable to determine method object reference. ", code, pos);
        pos = SkipWhiteSpace(code, pos);
        if (code[pos] == '.') pos++;
        var startPos = pos;
        while (pos < code.Length && (char.IsLetterOrDigit(code[pos]) || code[pos] == '_' || code[pos] == '.')) pos++;
        var methodName = code[startPos..pos];
        var lastDotInName = methodName.LastIndexOf('.');
        if (lastDotInName != -1) {
            var refName = methodName.Substring(0, lastDotInName);
            if (subject == null) {
                subject = new VariableReferenceSyntax(refName, code, startPos, startPos + refName.Length);
                methodName = methodName.Substring(lastDotInName + 1);
            } else {
                throw new Exception("Unknown error");
            }
        }
        var expression = new MethodCallSyntax(code, startPos, startPos) { Name = methodName, Subject = subject };
        var nextChar = code[pos];
        if (nextChar == '<') {
            var startGenerics = pos + 1;
            var posClosingGenerics = code.IndexOf('>', pos);
            if (posClosingGenerics == -1) throw new SyntaxException("Generic parameter not closed. ", code, pos);
            pos = posClosingGenerics;
            var genericArgs = code[startGenerics..pos];
            expression.GenericParams = genericArgs.Split(',', StringSplitOptions.TrimEntries).ToList();
            pos++; // skip >
            pos = SkipWhiteSpace(code, pos);
            nextChar = code[pos];
        }
        if (nextChar != '(') throw new SyntaxException("Was expecting bracket after method name. ", code, pos);
        pos++; // skip bracket
        pos = SkipWhiteSpace(code, pos);
        while (true) {
            pos = SkipWhiteSpace(code, pos);
            nextChar = code[pos];
            if (nextChar == ',') pos = SkipWhiteSpace(code, pos + 1);
            else if (nextChar == ')') break;
            var pa = ParseStatement(code, pos, out pos, null, parameters);
            if (pa == null) break;
            expression.Arguments.Add(pa);
        }
        pos = SkipWhiteSpace(code, pos);
        pos++; // skip bracket
        newPos = pos;
        expression.Pos2 = newPos;
        return expression;
    }
    public override string ToString() {
        return base.ToString() + (Subject == null ? "" : Subject + ".") + Name + (GenericParams.Count > 0 ? "<" + string.Join(", ", GenericParams) + ">" : "") + "(" + string.Join(", ", Arguments) + ")";
    }
    public override SyntaxUnitTypes SyntaxType => SyntaxUnitTypes.MethodCall;
}
public class LambdaSyntax : SyntaxUnit {
    public LambdaSyntax(string code, int pos1, int pos2) : base(code, pos1, pos2) {
    }
    public List<string>? Paramaters { get; set; }
    public SyntaxUnit? Body { get; set; }
    static public LambdaSyntax Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {
        pos = SkipWhiteSpace(code, pos);
        var expression = new LambdaSyntax(code, pos, pos);
        var usingBrackets = code[pos] == '(';
        if (usingBrackets) {
            pos++;
            pos = SkipWhiteSpace(code, pos);
        }
        var startOfArguments = pos;
        var endOfArguments = usingBrackets ? code.IndexOf(')', pos) : code.IndexOf('=', pos);
        if (endOfArguments - startOfArguments < 1) throw new SyntaxException("Expected lambda function. ", code, pos);
        var arguments = code[startOfArguments..endOfArguments].Split(',', StringSplitOptions.TrimEntries);

        for (int i = 0; i < arguments.Length; i++) arguments[i] = arguments[i].Split(' ').Last(); // removing type, ie: "Article a" to "a"
        expression.Paramaters = arguments.ToList();
        pos = code.IndexOf('>', endOfArguments);
        pos++;
        pos = SkipWhiteSpace(code, pos);
        var usingCurlyBrackets = code[pos] == '{';
        if (usingCurlyBrackets) throw new SyntaxException("Code sections is not supported (yet) in lambda expressions. ", code, pos); // could be added... but using
        if (usingCurlyBrackets) pos++;
        var body = ParseStatement(code, pos, out var endOfFunction, null, parameters);
        if (body == null) throw new NullReferenceException();
        expression.Body = body;
        pos = SkipWhiteSpace(code, endOfFunction);
        if (usingCurlyBrackets) {
            var lastChar = code[pos];
            if (lastChar != '}') throw new SyntaxException("Expected closing curly bracket. ", code, pos);
            pos++;
        }
        newPos = pos;
        expression.Pos2 = newPos;
        return expression;
    }
    public override string ToString() {
        if (Paramaters == null) return base.ToString() + "() => " + Body + "";
        return base.ToString() + "(" + string.Join(", ", Paramaters) + ") => " + Body + "";
    }
    public override SyntaxUnitTypes SyntaxType => SyntaxUnitTypes.LambdaDeclaration;
}
public class AnonymousObjectSyntax : SyntaxUnit {
    public AnonymousObjectSyntax(string code, int pos1, int pos2) : base(code, pos1, pos2) { }
    public List<string> Names { get; } = new();
    public List<SyntaxUnit> Values { get; } = new();
    static public AnonymousObjectSyntax Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {
        // new {a=122,b=3424}
        // new {a:122,b:3424}  // also supported....
        pos = SkipWhiteSpace(code, pos);
        var startPos = pos;
        if (pos + 3 > code.Length) throw new SyntaxException("Code ended unexpectedly. ", code, pos);
        var keyWord = code[pos..(pos + 3)];
        if (keyWord.Trim() != "new") throw new SyntaxException("Expected the new keyword. ", code, pos);
        pos += 3;
        pos = SkipWhiteSpace(code, pos);
        pos = code.IndexOf('{', pos);
        if (pos < -1) throw new SyntaxException("Expected opening curly bracket. ", code, pos);
        if (pos + 1 > code.Length) throw new SyntaxException("Code ended unexpectedly. ", code, pos);
        pos++;
        var expression = new AnonymousObjectSyntax(code, startPos, startPos);
        while (true) {
            pos = SkipWhiteSpace(code, pos);
            var startName = pos;
            var endOfName = code.IndexOfAnyOutsideStringLiterals(pos, '=', ':');
            var endOfProperty = code.IndexOfAnyOutsideStringLiterals(pos, ',', '}');
            if (endOfProperty == -1) throw new SyntaxException("Expected closing curly bracket. ", code, pos);
            string name;
            SyntaxUnit? valueExpression;
            if (endOfName == -1 || endOfProperty < endOfName) {
                // no name decleration, so use member access as name
                valueExpression = ParseStatement(code, pos, out newPos, null, parameters);
                if (valueExpression == null) throw new SyntaxException("Expected expression. ", code, pos);
                if (valueExpression is VariableReferenceSyntax rs) name = rs.MemberName;
                else throw new SyntaxException("Expected expression. ", code, pos);
            } else {
                name = code[startName..endOfName].Trim();
                valueExpression = ParseStatement(code, endOfName + 1, out newPos, null, parameters);
            }
            if (valueExpression == null) throw new SyntaxException("Expected expression. ", code, pos);
            expression.Names.Add(name);
            expression.Values.Add(valueExpression);
            pos = newPos;
            pos = SkipWhiteSpace(code, pos);
            if (pos + 1 > code.Length) throw new SyntaxException("Code ended unexpectedly. ", code, pos);
            var nextChar = code[pos];
            if (nextChar == '}') break; // end of contruction
            if (nextChar != '}' && code[pos] != ',') throw new SyntaxException("Expected closing curly bracket or comma. ", code, pos);
            // must be comma here, so continue..
            pos++;
        }
        newPos = pos + 1;
        expression.Pos2 = newPos;
        return expression;
    }
    public override string ToString() {
        var sb = new StringBuilder();
        sb.Append("new {");
        for (int n = 0; n < Names.Count; n++) {
            sb.Append(Names[n]);
            sb.Append("=");
            sb.Append(Values[n]);
            if (Names.Count - n > 1) sb.Append(", ");
        }
        sb.Append("}");
        return sb.ToString();
    }
    public override SyntaxUnitTypes SyntaxType => SyntaxUnitTypes.AnonymousObject;
}
public class ObjectConstructionSyntax : SyntaxUnit {
    public ObjectConstructionSyntax(string typeName, string code, int pos1, int pos2) : base(code, pos1, pos2) {
        TypeName = typeName;
    }
    public string TypeName { get; }
    public List<string>? GenericParams { get; set; }
    public List<SyntaxUnit> Arguments { get; set; } = new();
    static public ObjectConstructionSyntax Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {
        var pos1 = pos;
        pos = SkipWhiteSpace(code, pos);
        var keyWord = code[pos..(pos + 4)];
        if (keyWord != "new ") throw new SyntaxException("Expected the new keyword. ", code, pos);
        pos += 4;
        pos = SkipWhiteSpace(code, pos);
        var startPos = pos;
        while (pos < code.Length && (char.IsLetterOrDigit(code[pos]) || code[pos] == '.' || code[pos] == '_')) pos++;
        var typeName = code[startPos..pos];
        var nextChar = code[pos];
        var expression = new ObjectConstructionSyntax(typeName, code, pos1, pos1);
        if (nextChar == '<') {
            var startGenerics = pos + 1;
            while (pos < code.Length && code[pos] == '>') pos++;
            var genericArgs = code[startGenerics..pos];
            expression.GenericParams = genericArgs.Split(',', StringSplitOptions.TrimEntries).ToList();
            nextChar = code[pos + 1];
        }
        if (nextChar != '(') throw new SyntaxException("Was expecting bracket after method name. ", code, pos);
        while (true) {
            pos++;
            var arg = ParseStatement(code, pos, out pos, null, parameters);
            if (arg == null) throw new SyntaxException("Invalid argument. ", code, pos);
            expression.Arguments.Add(arg);
            pos = SkipWhiteSpace(code, pos);
            if (pos > code.Length) throw new SyntaxException("Code ended unexpectedly. ", code, pos);
            nextChar = code[pos];
            if (nextChar == ')') break;
            if (nextChar != ',') throw new SyntaxException("Expected comma. ", code, pos);
        }
        pos++;
        newPos = pos;
        expression.Pos2 = newPos;
        return expression;
    }
    public override string ToString() {
        return base.ToString() + "new" + TypeName
            + (GenericParams != null && GenericParams.Count > 0 ? "<" + string.Join(',', GenericParams) + ">" : "")
            + "(" + string.Join(',', Arguments) + ")";
    }
    public override SyntaxUnitTypes SyntaxType => SyntaxUnitTypes.ObjectConstruction;
}
public class OperatorExpressionSyntax : SyntaxUnit {
    public OperatorExpressionSyntax(string operatorName, SyntaxUnit leftValue, SyntaxUnit rightValue, string code, int pos1)
        : base(code, pos1, pos1) {
        Values = new() { leftValue, rightValue };
        Operators = new() { operatorName };
    }
    public static bool IsOperatorChar(char c) {
        return c == '!' || c == '|' || c == '&' || c == '+' || c == '-' || c == '/' || c == '*' || c == '<' || c == '>' || c == '=';
    }
    public List<SyntaxUnit> Values { get; }
    public List<string> Operators { get; } // + - * /
    public bool HasEvaluatedOperatorOrder;
    void Add(SyntaxUnit unit, string operatorName) {
        Values.Add(unit);
        Operators.Add(operatorName);
    }
    static public OperatorExpressionSyntax Parse(string code, int pos, out int newPos, SyntaxUnit? prevUnit, IEnumerable<Parameter> parameters) {
        if (prevUnit == null) throw new SyntaxException("Error in operator expression. ", code, pos);
        var startPosOp = pos;
        while (pos < code.Length && IsOperatorChar(code[pos])) pos++;
        var operatorName = code[startPosOp..pos];
        pos = SkipWhiteSpace(code, pos);
        var nextUnit = ParseUnit(code, pos, out newPos, null, parameters);
        if (nextUnit == null) throw new SyntaxException("Missing right side of operator. ", code, pos);
        var opEx = new OperatorExpressionSyntax(operatorName, prevUnit, nextUnit, code, pos);
        pos = newPos;
        opEx.Pos2 = newPos;
        while (newPos < code.Length && whatIsNext(code, newPos, nextUnit) == SyntaxUnitTypes.OperatorExpression) {
            pos = SkipWhiteSpace(code, pos);
            startPosOp = pos;
            while (pos < code.Length && IsOperatorChar(code[pos])) pos++;
            operatorName = code[startPosOp..pos];
            nextUnit = ParseUnit(code, pos, out pos, null, parameters);
            if (nextUnit == null) throw new NullReferenceException();
            opEx.Add(nextUnit, operatorName);
            newPos = pos;
            opEx.Pos2 = newPos;
        }
        return opEx;
    }
    public override string ToString() {
        var sb = new StringBuilder();
        sb.Append(base.ToString());
        sb.Append('(');
        for (var n = 0; n < Values.Count - 1; n++) {
            sb.Append(Values[n].ToString());
            sb.Append(Operators[n]);
        }
        sb.Append(Values.Last().ToString());
        sb.Append(')');
        return sb.ToString();
    }
    public override SyntaxUnitTypes SyntaxType => SyntaxUnitTypes.OperatorExpression;
}
public class BracketSyntax : SyntaxUnit {
    public BracketSyntax(SyntaxUnit content, string code, int pos1, int pos2) : base(code, pos1, pos2) {
        Content = content;
    }
    public SyntaxUnit Content { get; }
    static public BracketSyntax Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {
        pos = SkipWhiteSpace(code, pos);
        var startPos = pos;
        if (code[pos] != '(') throw new SyntaxException(code, pos);
        pos++; // skip bracket
        var exp = ParseStatement(code, pos, out pos, null, parameters);
        if (exp == null) throw new SyntaxException("Empty bracket. ", code, pos);
        pos = SkipWhiteSpace(code, pos);
        if (code[pos] != ')') throw new SyntaxException("Expected closing bracket. ", code, pos);
        pos++; // skip bracket
        newPos = pos;
        return new BracketSyntax(exp, code, startPos, newPos);
    }
    public override string ToString() {
        return base.ToString() + "(" + Content.ToString() + ")";
    }
    public override SyntaxUnitTypes SyntaxType => SyntaxUnitTypes.ExpressionBracket;
}
public class PreFixSyntax : SyntaxUnit {
    public PreFixSyntax(string prefix, SyntaxUnit value, string code, int pos1)
        : base(code, pos1, pos1) {
        Value = value;
        Prefix = prefix;
    }
    public static bool IsOperatorChar(char c) {
        return c == '!' || c == '-';
    }
    public SyntaxUnit Value { get; }
    public string Prefix { get; } // + - * /
    public bool HasEvaluatedOperatorOrder;
    static public PreFixSyntax Parse(string code, int pos, out int newPos, IEnumerable<Parameter> parameters) {
        var startPosOp = pos;
        while (pos < code.Length && IsOperatorChar(code[pos])) pos++;
        var prefix = code[startPosOp..pos];
        pos = SkipWhiteSpace(code, pos);
        var nextUnit = ParseUnit(code, pos, out newPos, null, parameters);
        if (nextUnit == null) throw new SyntaxException("No value after prefix operator. ", code, pos);
        var opEx = new PreFixSyntax(prefix, nextUnit, code, pos);
        opEx.Pos2 = newPos;
        return opEx;
    }
    public override string ToString() {
        var sb = new StringBuilder();
        sb.Append(base.ToString());
        sb.Append(Prefix);
        sb.Append(Value);
        return sb.ToString();
    }
    public override SyntaxUnitTypes SyntaxType => SyntaxUnitTypes.PreFixOperatorExpression;
}
