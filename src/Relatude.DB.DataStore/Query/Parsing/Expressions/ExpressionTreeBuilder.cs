using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query.Expressions;
using Relatude.DB.Query.Methods;
using Relatude.DB.Query.Parsing.Tokens;
namespace Relatude.DB.Query.Parsing.Expressions;
/// <summary>
/// The purpose of this class is to build an expression tree from a token tree.
/// The token tree focus on just parsing the string correctly.
/// While this class builds a tree of IExpressions that can be evaluated.
/// </summary>
public class ExpressionTreeBuilder {
    public static IExpression Build(TokenBase token, Datamodel dm) {
        return token.TokenType switch {
            TokenTypes.Empty => throw new ArgumentException("Empty expression. "),
            TokenTypes.ValueConstant => BuildValueConstant((ValueConstantToken)token),
            TokenTypes.OperatorExpression => BuildOperator((OperatorExpressionToken)token, dm),
            TokenTypes.Variable => BuildVariableReference((VariableReferenceToken)token),
            TokenTypes.MethodCall => BuildMethod.BuildMethodCall((MethodCallToken)token, dm),
            TokenTypes.AnonymousObject => BuildAnonymousObject((AnonymousObjectToken)token, dm),
            TokenTypes.ExpressionBracket => BuildBracket((BracketToken)token, dm),
            TokenTypes.LambdaDeclaration => BuildLambda((LambdaToken)token, dm),
            TokenTypes.PreFixOperatorExpression => BuildPreFixOperator((PreFixToken)token, dm),
            TokenTypes.ObjectConstruction => throw new NotImplementedException(),
            _ => throw new NotImplementedException(),
        };
    }
    public static IConstantExpression BuildValueConstant(ValueConstantToken constantValue) {
        switch (constantValue.ParsedTypeHint) {
            case ParsedTypes.FromParameter: {
                    // value stems from parameter substitution...
                    var value = constantValue.DirectValue;
                    if (value == null) return new NullConstantExpression();
                    if (value is bool b) return new BooleanConstantExpression(b);
                    if (value is string s) return new StringConstantExpression(s);
                    if (value is int i) return new IntegerConstantExpression(i);
                    if (value is long lng) return new DecimalConstantExpression(lng);
                    if (value is double d) return new DoubleConstantExpression(d);
                    if (value is decimal dec) return new DecimalConstantExpression(dec);
                    throw new NotSupportedException("Parameter of type " + value.GetType().Name + " is not yet supported as paramater expression. ");
                }
            case ParsedTypes.Null: return new NullConstantExpression();
            case ParsedTypes.Boolean: return new BooleanConstantExpression(constantValue.GetBoolValue());
            case ParsedTypes.String: return new StringConstantExpression(constantValue.GetStringValue());
            case ParsedTypes.LongString: return new LongConstantExpression(constantValue.GetLongValue());
            case ParsedTypes.IntString: return new IntegerConstantExpression(constantValue.GetIntValue());
            case ParsedTypes.FloatString: return new DoubleConstantExpression(constantValue.GetDoubleValue());
            default: throw new NotSupportedException("Parameter of type " + constantValue.ParsedTypeHint + " is not yet supported as parsed expression.");
        }
    }
    public static IExpression BuildPreFixOperator(PreFixToken e, Datamodel dm) {
        var c = Build(e.Value, dm);
        if (e.Prefix == "-") return new MinusPrefixExpression(c);
        if (e.Prefix == "!") return new NotPrefixExpression(c);
        throw new Exception("Prefix operator not supported: " + e.Prefix);
    }
    public static OperatorExpression BuildOperator(OperatorExpressionToken operatorExpression, Datamodel dm) {
        var expressions = operatorExpression.Values.Select(v => Build(v, dm)).ToList();
        var operators = operatorExpression.Operators.Select(o => OperatorUtil.Parse(o)).ToList();
        return new OperatorExpression(expressions, operators, true);
    }
    public static IExpression BuildVariableReference(VariableReferenceToken e) {
        var posDot = e.Name.IndexOf('.');
        if (posDot > -1) {
            var first = e.Name[..posDot];
            var rest = e.Name[(posDot + 1)..].Trim();
            var varRef = new VariableReferenceExpression(first);
            return new PropertyReferenceExpression(varRef, rest);
        } else {
            return new VariableReferenceExpression(e.Name);
        }
    }
    public static LambdaExpression BuildLambda(LambdaToken e, Datamodel dm) {
        if (e.Body == null) throw new NullReferenceException();
        if (e.Paramaters == null) throw new NullReferenceException();
        IExpression func = Build(e.Body, dm);
        var ex = new LambdaExpression(e.Paramaters, func);
        return ex;
    }
    public static AnonymousObjectExpression BuildAnonymousObject(AnonymousObjectToken e, Datamodel dm) {
        var valueExpressions = new List<IExpression>();
        foreach (var valueExp in e.Values) valueExpressions.Add(Build(valueExp, dm));
        var props = e.Names.Select(n => new KeyValuePair<string, PropertyType>(n, PropertyType.Any)).ToArray();
        var ex = new AnonymousObjectExpression(props, valueExpressions);
        return ex;
    }
    public static OperatorExpression BuildBracket(BracketToken e, Datamodel dm) {
        var c = Build(e.Content, dm);
        if (c is BracketExpression b) return b;
        return new BracketExpression(c);
    }
}
