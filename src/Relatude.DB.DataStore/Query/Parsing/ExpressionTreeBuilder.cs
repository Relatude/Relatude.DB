using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query.Expressions;
using Relatude.DB.Query.Methods;
namespace Relatude.DB.Query.Parsing;
delegate IExpression BuildMethod(SyntaxUnit syntax, Datamodel dm);
public class ExpressionBuilderException : Exception {
    readonly SyntaxUnit _syntaxtUnit;
    readonly string? _message;
    public ExpressionBuilderException(SyntaxUnit syntaxUnit) {
        _syntaxtUnit = syntaxUnit;
    }
    public ExpressionBuilderException(string message, SyntaxUnit syntaxUnit) {
        _syntaxtUnit = syntaxUnit;
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
            var p1 = _syntaxtUnit.Pos1;
            var p2 = _syntaxtUnit.Pos2;
            var c = _syntaxtUnit.Code;
            // better code can be added here for text extraction...
            if (p1 <= 0) p1 = 0;
            else if (p1 >= c.Length) p1 = c.Length - 1;
            return (_message == null ? "" : _message)
                + "Unexpected expression at pos " + p1 + ".." + p2 + " : "
                + (p1 - padding > 0 ? "..." : "")
                + extract(p1 - padding, p1, c)
                //+ _code[_pos] + "\u0333"
                + " ==> " + c[p1] + " <== "
                + extract(p1 + 1, p1 + padding, c)
                + (p1 + padding < c.Length ? "..." : "");
        }
    }
}
/// <summary>
/// The purpose of this class is to build an expression tree from a syntax tree.
/// The syntax tree focus on just parsing the string correctly.
/// While this class builds a tree of IExpressions that can be evaluated.
/// </summary>
public class ExpressionTreeBuilder {
    public static IExpression Build(SyntaxUnit syntax, Datamodel dm) {
        return syntax.SyntaxType switch {
            SyntaxUnitTypes.Empty => throw new ArgumentException("Empty expression. "),
            SyntaxUnitTypes.ValueConstant => BuildValueConstant((ValueConstantSyntax)syntax),
            SyntaxUnitTypes.OperatorExpression => BuildOperator((OperatorExpressionSyntax)syntax, dm),
            SyntaxUnitTypes.Variable => BuildVariableReference((VariableReferenceSyntax)syntax),
            SyntaxUnitTypes.MethodCall => BuildMethodCall((MethodCallSyntax)syntax, dm),
            SyntaxUnitTypes.AnonymousObject => BuildAnonymousObject((AnonymousObjectSyntax)syntax, dm),
            SyntaxUnitTypes.ExpressionBracket => BuildBracket((BracketSyntax)syntax, dm),
            SyntaxUnitTypes.LambdaDeclaration => BuildLambda((LambdaSyntax)syntax, dm),
            SyntaxUnitTypes.PreFixOperatorExpression => BuildPreFixOperator((PreFixSyntax)syntax, dm),
            SyntaxUnitTypes.ObjectConstruction => throw new NotImplementedException(),
            _ => throw new NotImplementedException(),
        };
    }
    static IConstantExpression BuildValueConstant(ValueConstantSyntax constantValue) {
        switch (constantValue.ParsedType) {
            case ParsedTypes.NotParsed: {
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
            case ParsedTypes.IntegerNumberString: return new IntegerConstantExpression(constantValue.GetIntValue());
            case ParsedTypes.FloatingNumberString: return new DoubleConstantExpression(constantValue.GetDoubleValue());
            default: throw new NotSupportedException("Parameter of type " + constantValue.ParsedType + " is not yet supported as parsed expression.");
        }
    }
    static IExpression BuildPreFixOperator(PreFixSyntax e, Datamodel dm) {
        var c = Build(e.Value, dm);
        if (e.Prefix == "-") return new MinusPrefixExpression(c);
        if (e.Prefix == "!") return new NotPrefixExpression(c);
        throw new Exception("Prefix operator not supported: " + e.Prefix);
    }
    static OperatorExpression BuildOperator(OperatorExpressionSyntax operatorExpression, Datamodel dm) {
        var expressions = operatorExpression.Values.Select(v => Build(v, dm)).ToList();
        var operators = operatorExpression.Operators.Select(o => OperatorUtil.Parse(o)).ToList();
        return new OperatorExpression(expressions, operators, true);
    }
    static IExpression BuildVariableReference(VariableReferenceSyntax e) {
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
    static IExpression BuildMethodCall(MethodCallSyntax e, Datamodel dm) {
        var name = e.Name.ToLower();
        if (name == "select") {
            if (e.Arguments.Count != 1) throw new Exception("Select statement only accepts one argument. ");
            var arg = e.Arguments[0];
            if (arg is not LambdaSyntax lambda) throw new Exception("Select statement only accept lambda expressions as argument. ");
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (lambda.Paramaters == null) throw new NullReferenceException();
            if (lambda.Paramaters.Count != 1) throw new Exception("Select statement only accepts a lambda expression with one parameter. ");
            LambdaExpression lambdaEx = BuildLambda(lambda, dm);
            return new SelectMethod(source, lambdaEx);
        }
        if (name == "selectid") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            return new SelectIdMethod(source);
        }
        if (name == "where") {
            var arg = e.Arguments[0];
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (arg is LambdaSyntax lambda) {
                if (e.Arguments.Count != 1) throw new Exception("Where statement only accepts one argument. ");
                if (lambda.Paramaters == null) throw new NullReferenceException();
                if (lambda.Paramaters.Count != 1) throw new Exception("Where statement only accepts a lambda expression with one parameter. ");
                LambdaExpression lambdaEx = BuildLambda(lambda, dm);
                return new WhereMethod(source, lambdaEx);
            } else {
                throw new Exception("Where statement only accept lambda expressions or strings as argument. ");
            }
        }
        if (name == "wheretypes") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (e.Arguments.Count > 0 && e.Arguments[0] is ValueConstantSyntax typeIds) {
                return new WhereTypesMethod(source, typeIds.GetNodeTypeGuids(dm));
            } else {
                throw new Exception("WhereTypes statement only accepts one parameter. ");
            }
        }
        if (name == "orderby") {
            var arg = e.Arguments[0];
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (arg is LambdaSyntax lambda) {
                if (lambda.Paramaters == null) throw new NullReferenceException();
                if (lambda.Paramaters.Count != 1) throw new Exception("Where statement only accepts a lambda expression with one parameter. ");
                var descending = e.Arguments.Count > 1 && (e.Arguments[1] + "").ToLower() == "true";
                LambdaExpression lambdaEx = BuildLambda(lambda, dm);
                return new OrderByMethod(source, lambdaEx, descending);
            } else {
                throw new Exception("OrderBy statement only accept lambda expressions or strings as argument. ");
            }
        }
        if (name == "facets") {
            if (e.Subject == null) throw new NullReferenceException();
            foreach (var arg in e.Arguments) if (arg is not ValueConstantSyntax) throw new Exception("Only string arguments allowed in facet expression. ");
            var source = Build(e.Subject, dm);
            return new FacetMethod(source, e.Arguments.Cast<ValueConstantSyntax>().Select(a => a.GetStringValue()), dm);
        }
        if (name == "addfacet") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantSyntax arg) throw new Exception("Only string arguments allowed in facet expression. ");
            fc.AddFacet(arg.GetStringValue());
            return fc;
        }
        if (name == "addvaluefacet") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantSyntax arg) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments.Count == 1) {
                fc.AddValueFacet(arg.GetStringValue());
            } else {
                if (e.Arguments[1] is not ValueConstantSyntax arg2) throw new Exception("Only string arguments allowed in facet expression. ");
                fc.AddValueFacet(arg.GetStringValue(), arg2.GetStringValue());
            }
            return fc;
        }
        if (name == "addrangefacet") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantSyntax arg) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments.Count == 1) {
                fc.AddRangeFacet(arg.GetStringValue());
            } else {
                if (e.Arguments[1] is not ValueConstantSyntax arg2) throw new Exception("Only string arguments allowed in facet expression. ");
                if (e.Arguments[2] is not ValueConstantSyntax arg3) throw new Exception("Only string arguments allowed in facet expression. ");
                fc.AddRangeFacet(arg.GetStringValue(), arg2.GetStringValue(), arg3.GetStringValue());
            }
            return fc;
        }
        if (name == "setfacetvalue") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantSyntax arg1) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments[1] is not ValueConstantSyntax arg2) throw new Exception("Only string arguments allowed in facet expression. ");
            fc.SetFacetValue(arg1.GetStringValue(), arg2.GetStringValue());
            return fc;
        }
        if (name == "setfacetrangevalue") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantSyntax arg1) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments[1] is not ValueConstantSyntax arg2) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments[2] is not ValueConstantSyntax arg3) throw new Exception("Only string arguments allowed in facet expression. ");
            fc.SetFacetRangeValue(arg1.GetStringValue(), arg2.GetStringValue(), arg3.GetStringValue());
            return fc;
            throw new NotSupportedException("The method \"" + e + "\" is not supported. ");
        }
        if (name == "search" || name == "wheresearch") {
            if (e.Subject == null) throw new NullReferenceException();
            foreach (var arg in e.Arguments) if (arg is not ValueConstantSyntax) throw new Exception("Only string argument allowed in search expression. ");
            var source = Build(e.Subject, dm);
            if (e.Arguments.Count < 1) throw new Exception("Missing search parameter. ");
            var searchTextO = (ValueConstantSyntax)e.Arguments[0];
            double? semanticRatio = null;
            if (e.Arguments.Count > 1) {
                var semanticRatioO = (ValueConstantSyntax)e.Arguments[1];
                semanticRatio = semanticRatioO.GetDoubleOrNullValue();
            }
            float? minimumVectorSimilarity = null;
            if (e.Arguments.Count > 2) {
                var minimumVectorSimilarityO = (ValueConstantSyntax)e.Arguments[2];
                minimumVectorSimilarity = minimumVectorSimilarityO.GetFloatOrNullValue();
            }
            bool? orSearch = null;
            if (e.Arguments.Count > 3) {
                var orSearchO = (ValueConstantSyntax)e.Arguments[3];
                orSearch = orSearchO.GetBoolOrNullValue();
            }
            int? maxWordsEvaluated = null;
            if (e.Arguments.Count > 4) {
                var maxWordsEvaluatedO = (ValueConstantSyntax)e.Arguments[4];
                maxWordsEvaluated = maxWordsEvaluatedO.GetIntOrNullValue();
            }
            int? maxHitsEvaluated = null;
            if (e.Arguments.Count > 5) {
                var maxHitsEvaluatedO = (ValueConstantSyntax)e.Arguments[5];
                maxHitsEvaluated = maxHitsEvaluatedO.GetIntOrNullValue();
            }
            if (name == "wheresearch") {
                return new WhereSearchMethod(source, searchTextO.GetStringValue(), semanticRatio, minimumVectorSimilarity, orSearch, maxWordsEvaluated);
            } else {
                return new SearchMethod(source, searchTextO.GetStringValue(), semanticRatio, minimumVectorSimilarity, orSearch, maxWordsEvaluated, maxHitsEvaluated);
            }
        }
        if (name == "page") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (e.Arguments.Count != 2) throw new Exception("Page statement only accepts two parameters. ");
            var p1 = ((ValueConstantSyntax)e.Arguments[0]).GetIntValue();
            var p2 = ((ValueConstantSyntax)e.Arguments[1]).GetIntValue();
            return new PageMethod(source, p1, p2);
        }
        if (name == "take") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (e.Arguments.Count != 1) throw new Exception("Take statement only accepts one parameter. ");
            var p1 = ((ValueConstantSyntax)e.Arguments[0]).GetIntValue();
            return new TakeMethod(source, p1);
        }
        if (name == "skip") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (e.Arguments.Count != 1) throw new Exception("Skip statement only accepts one parameter. ");
            var p1 = ((ValueConstantSyntax)e.Arguments[0]).GetIntValue();
            return new SkipMethod(source, p1);
        }
        if (name == "count") {
            if (e.Arguments.Count > 0) throw new Exception("Count does not accepts arguments. ");
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            var s = new CountMethod(source);
            return s;
        }
        if (name == "sum") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            LambdaExpression? lambdaEx = null;
            if (e.Arguments.Count > 0) {
                var arg = e.Arguments[0];
                if (arg is not LambdaSyntax lambda) throw new Exception("Sum statement only accept lambda expressions as argument. ");
                if (lambda.Paramaters == null) throw new NullReferenceException();
                if (lambda.Paramaters.Count != 1) throw new Exception("Sum statement only accepts a lambda expression with one parameter. ");
                lambdaEx = BuildLambda(lambda, dm);
            }
            return new SumMethod(source, lambdaEx);
        }
        if (name == "any") {
            if (e.Subject == null) throw new NullReferenceException();
            var path = (e.Subject as VariableReferenceSyntax)?.Name;
            if (path == null) throw new NullReferenceException();
            return new RelationExpression(path, e.Arguments[0].ToString(), name);
        }
        if (name == "is" || name == "has") {
            if (e.Subject == null) throw new NullReferenceException();
            var path = (e.Subject as VariableReferenceSyntax)?.Name;
            if (path == null) throw new NullReferenceException();
            return new RelationExpression(path, e.Arguments[0].ToString(), name);
        }
        if (name == "wherein") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            List<string> branch = new();
            if (e.Arguments[0] is not ValueConstantSyntax prop) throw new Exception("Only string arguments allowed as property in relation expression. ");
            if (e.Arguments.Count > 1 && e.Arguments[1] is ValueConstantSyntax id) {
                var propId = prop.GetPropertyId(dm);
                return new WhereInMethod(source, propId, id.GetPropertyValues(dm, propId));
            } else {
                throw new Exception("Relates statement only accepts two parameters. ");
            }
        }
        if (name == "whereinids") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            List<string> branch = new();
            if (e.Arguments.Count > 0 && e.Arguments[0] is ValueConstantSyntax id) {
                return new WhereInIdsMethod(source, id.GetStringValue());
            } else {
                throw new Exception("Relates statement only accepts one parameter. ");
            }
        }
        if (name == "relatesany") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            List<string> branch = new();
            if (e.Arguments[0] is not ValueConstantSyntax prop) throw new Exception("Only string arguments allowed as property in relation expression. ");
            if (e.Arguments.Count > 1 && e.Arguments[1] is ValueConstantSyntax id) {
                return new RelatesAnyMethod(source, dm, prop.GetStringValue(), id.GetStringValue());
            } else {
                throw new Exception("Relates statement only accepts two parameters. ");
            }
        }
        if (name == "relates") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            List<string> branch = new();
            if (e.Arguments[0] is not ValueConstantSyntax prop) throw new Exception("Only string arguments allowed as property in relation expression. ");
            if (e.Arguments.Count > 1 && e.Arguments[1] is ValueConstantSyntax id) {
                return new RelatesMethod(source, dm, prop.GetStringValue(), id.GetStringValue());
            } else {
                throw new Exception("Relates statement only accepts two parameters. ");
            }
        }
        if (name == "relatesnot") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            List<string> branch = new();
            if (e.Arguments[0] is not ValueConstantSyntax prop) throw new Exception("Only string arguments allowed as property in relation expression. ");
            if (e.Arguments.Count > 0 && e.Arguments[1] is ValueConstantSyntax id) {
                return new RelatesNotMethod(source, dm, prop.GetStringValue(), id.GetStringValue());
            } else {
                throw new Exception("Relates statement only accepts two parameters. ");
            }
        }
        if (name == "include") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            List<string> branch = new();
            if (e.Arguments[0] is not ValueConstantSyntax prop) throw new Exception("Only string arguments allowed as property in include expression. ");
            return new IncludeMethod(source, dm, prop.GetStringValue());
        }
        if (name == "inrange") {
            if (e.Subject == null) throw new NullReferenceException();
            var path = (e.Subject as VariableReferenceSyntax)?.Name;
            if (path == null) throw new NullReferenceException();
            return new RangeExpression(path, e.Arguments[0].ToString(), e.Arguments[1].ToString());
        }
        throw new NotSupportedException("The method \"" + e + "\" is not supported. ");
    }
    static LambdaExpression BuildLambda(LambdaSyntax e, Datamodel dm) {
        if (e.Body == null) throw new NullReferenceException();
        if (e.Paramaters == null) throw new NullReferenceException();
        IExpression func = Build(e.Body, dm);
        var ex = new LambdaExpression(e.Paramaters, func);
        return ex;
    }
    static AnonymousObjectExpression BuildAnonymousObject(AnonymousObjectSyntax e, Datamodel dm) {
        var valueExpressions = new List<IExpression>();
        foreach (var valueExp in e.Values) valueExpressions.Add(Build(valueExp, dm));
        var props = e.Names.Select(n => new KeyValuePair<string, PropertyType>(n, PropertyType.Any)).ToArray();
        var ex = new AnonymousObjectExpression(props, valueExpressions);
        return ex;
    }
    static OperatorExpression BuildBracket(BracketSyntax e, Datamodel dm) {
        var c = Build(e.Content, dm);
        if (c is BracketExpression b) return b;
        return new BracketExpression(c);
    }
}
