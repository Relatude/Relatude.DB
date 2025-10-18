using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;
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
            //SyntaxUnitTypes.ValueConstant => BuildValueConstant((ValueConstantSyntax)syntax),
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
    //static IConstantExpression BuildValueConstant(ValueConstantSyntax constantValue) {
    //    var valueAsString = constantValue.ValueAsString;
    //    if (constantValue.InQuotes) return new StringConstantExpression(valueAsString);
    //    if (bool.TryParse(valueAsString, out var bv)) return new BooleanConstantExpression(bv);
    //    if (int.TryParse(valueAsString, out var iv)) return new IntegerConstantExpression(iv);
    //    if (long.TryParse(valueAsString, out var lng)) return new LongConstantExpression(lng);
    //    if (decimal.TryParse(valueAsString, out var dec)) return new DecimalConstantExpression(dec);
    //    if (double.TryParse(valueAsString, CultureInfo.InvariantCulture, out var dv)) return new DoubleConstantExpression(dv);
    //    if (valueAsString == "null") return new NullConstantExpression();
    //    throw new ExpressionBuilderException("Unable to parse to valuetype: " + valueAsString, constantValue);
    //}
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
                Guid[] typeGuids;
                if (typeIds.Value is Guid[] ids) {
                    typeGuids = ids;
                } else if (typeIds.Value is string[] typeNames) {
                    typeGuids = new Guid[typeNames.Length];
                    for (var i = 0; i < typeNames.Length; i++) {
                        var typeName = typeNames[i];
                        if (Guid.TryParse(typeName, out var id)) {
                        } else if (dm.NodeTypesByFullName.TryGetValue(typeName, out var nodeType)) {
                            id = nodeType.Id;
                        } else if (dm.NodeTypesByShortName.TryGetValue(typeName, out var nodeTypes)) {
                            if (nodeTypes.Length > 1) throw new Exception("Type name is ambiguous: " + typeName);
                            id = nodeTypes[0].Id;
                        } else {
                            throw new Exception("Type not found: " + typeName);
                        }
                        typeGuids[i] = id;
                    }
                } else {
                    throw new Exception("WhereTypes statement only accepts a string array as argument. ");
                }
                return new WhereTypesMethod(source, typeGuids);
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
            return new FacetMethod(source, e.Arguments.Cast<ValueConstantSyntax>().Select(a => (string)a.ValueNotNull), dm);
        }
        if (name == "addfacet") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantSyntax arg) throw new Exception("Only string arguments allowed in facet expression. ");
            fc.AddFacet((string)arg.ValueNotNull);
            return fc;
        }
        if (name == "addvaluefacet") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantSyntax arg) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments.Count == 1) {
                fc.AddValueFacet((string)arg.ValueNotNull);
            } else {
                if (e.Arguments[1] is not ValueConstantSyntax arg2) throw new Exception("Only string arguments allowed in facet expression. ");
                fc.AddValueFacet((string)arg.ValueNotNull, (string)arg2.ValueNotNull);
            }
            return fc;
        }
        if (name == "addrangefacet") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantSyntax arg) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments.Count == 1) {
                fc.AddRangeFacet((string)arg.ValueNotNull);
            } else {
                if (e.Arguments[1] is not ValueConstantSyntax arg2) throw new Exception("Only string arguments allowed in facet expression. ");
                if (e.Arguments[2] is not ValueConstantSyntax arg3) throw new Exception("Only string arguments allowed in facet expression. ");
                fc.AddRangeFacet((string)arg.ValueNotNull, (string)arg2.ValueNotNull, (string)arg3.ValueNotNull);
            }
            return fc;
        }
        if (name == "setfacetvalue") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantSyntax arg1) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments[1] is not ValueConstantSyntax arg2) throw new Exception("Only string arguments allowed in facet expression. ");
            fc.SetFacetValue((string)arg1.ValueNotNull, (string)arg2.ValueNotNull);
            return fc;
        }
        if (name == "setfacetrangevalue") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantSyntax arg1) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments[1] is not ValueConstantSyntax arg2) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments[2] is not ValueConstantSyntax arg3) throw new Exception("Only string arguments allowed in facet expression. ");
            fc.SetFacetRangeValue((string)arg1.ValueNotNull, (string)arg2.ValueNotNull, (string)arg3.ValueNotNull);
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
                semanticRatio = (double?)semanticRatioO.Value;
            }
            float? minimumVectorSimilarity = null;
            if (e.Arguments.Count > 2) {
                var minimumVectorSimilarityO = (ValueConstantSyntax)e.Arguments[2];
                minimumVectorSimilarity = (float?)minimumVectorSimilarityO.Value;
            }
            bool? orSearch = null;
            if (e.Arguments.Count > 3) {
                var orSearchO = (ValueConstantSyntax)e.Arguments[3];
                orSearch = (bool?)orSearchO.Value;
            }
            int? maxWordsEvaluated = null;
            if (e.Arguments.Count > 4) {
                var maxWordsEvaluatedO = (ValueConstantSyntax)e.Arguments[4];
                maxWordsEvaluated = (int?)maxWordsEvaluatedO.Value;
            }
            int? maxHitsEvaluated = null;
            if (e.Arguments.Count > 5) {
                var maxHitsEvaluatedO = (ValueConstantSyntax)e.Arguments[5];
                maxHitsEvaluated = (int?)maxHitsEvaluatedO.Value;
            }
            if (name == "wheresearch") {
                return new WhereSearchMethod(source, (string)searchTextO.ValueNotNull, semanticRatio, minimumVectorSimilarity, orSearch, maxWordsEvaluated);
            } else {
                return new SearchMethod(source, (string)searchTextO.ValueNotNull, semanticRatio, minimumVectorSimilarity, orSearch, maxWordsEvaluated, maxHitsEvaluated);
            }
        }
        if (name == "page") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (e.Arguments.Count != 2) throw new Exception("Page statement only accepts two parameters. ");
            var p1 = int.Parse(((ValueConstantSyntax)e.Arguments[0]).ValueAsString);
            var p2 = int.Parse(((ValueConstantSyntax)e.Arguments[1]).ValueAsString);
            return new PageMethod(source, p1, p2);
        }
        if (name == "take") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (e.Arguments.Count != 1) throw new Exception("Take statement only accepts one parameter. ");
            var p1 = int.Parse(((ValueConstantSyntax)e.Arguments[0]).ValueAsString);
            return new TakeMethod(source, p1);
        }
        if (name == "skip") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            if (e.Arguments.Count != 1) throw new Exception("Skip statement only accepts one parameter. ");
            var p1 = int.Parse(((ValueConstantSyntax)e.Arguments[0]).ValueAsString);
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
                return new WhereInMethod(source, dm, prop.ValueAsString, id.ValueAsString);
            } else {
                throw new Exception("Relates statement only accepts two parameters. ");
            }
        }
        if (name == "whereinids") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            List<string> branch = new();
            if (e.Arguments.Count > 0 && e.Arguments[0] is ValueConstantSyntax id) {
                return new WhereInIdsMethod(source, id.ValueAsString);
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
                return new RelatesAnyMethod(source, dm, prop.ValueAsString, id.ValueAsString);
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
                return new RelatesMethod(source, dm, prop.ValueAsString, id.ValueAsString);
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
                return new RelatesNotMethod(source, dm, prop.ValueAsString, id.ValueAsString);
            } else {
                throw new Exception("Relates statement only accepts two parameters. ");
            }
        }
        if (name == "include") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = Build(e.Subject, dm);
            List<string> branch = new();
            if (e.Arguments[0] is not ValueConstantSyntax prop) throw new Exception("Only string arguments allowed as property in include expression. ");
            return new IncludeMethod(source, dm, prop.ValueAsString);
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
