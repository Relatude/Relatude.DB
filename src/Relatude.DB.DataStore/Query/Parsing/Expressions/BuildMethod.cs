using Relatude.DB.Datamodels;
using Relatude.DB.Query.Expressions;
using Relatude.DB.Query.Methods;
using Relatude.DB.Query.Parsing.Tokens;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.DB.Query.Parsing.Expressions;
class ExpressionMethod {
    public required string Name { get; set; }
    public required ExpressionMethodParameter[] Parameters { get; set; }
    public required Func<object[], IExpression> Create { get; set; }
}
class ExpressionMethodParameter {
    public required object ParameterType { get; set; }
    public required bool IsOptional { get; set; }
    public required bool AllowNull { get; set; }
}
internal class BuildMethod {
    public static IExpression BuildMethodCall(MethodCallToken e, Datamodel dm) {
        var name = e.Name.ToLower();
        if (name == "select") {
            if (e.Arguments.Count != 1) throw new Exception("Select statement only accepts one argument. ");
            var arg = e.Arguments[0];
            if (arg is not LambdaToken lambda) throw new Exception("Select statement only accept lambda expressions as argument. ");
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            if (lambda.Paramaters == null) throw new NullReferenceException();
            if (lambda.Paramaters.Count != 1) throw new Exception("Select statement only accepts a lambda expression with one parameter. ");
            LambdaExpression lambdaEx = ExpressionTreeBuilder.BuildLambda(lambda, dm);
            return new SelectMethod(source, lambdaEx);
        }
        if (name == "selectid") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            return new SelectIdMethod(source);
        }
        if (name == "where") {
            var arg = e.Arguments[0];
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            if (arg is LambdaToken lambda) {
                if (e.Arguments.Count != 1) throw new Exception("Where statement only accepts one argument. ");
                if (lambda.Paramaters == null) throw new NullReferenceException();
                if (lambda.Paramaters.Count != 1) throw new Exception("Where statement only accepts a lambda expression with one parameter. ");
                LambdaExpression lambdaEx = ExpressionTreeBuilder.BuildLambda(lambda, dm);
                return new WhereMethod(source, lambdaEx);
            } else {
                throw new Exception("Where statement only accept lambda expressions or strings as argument. ");
            }
        }
        if (name == "wheretypes") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            if (e.Arguments.Count > 0 && e.Arguments[0] is ValueConstantToken arg1) {
                bool includeDescendants = e.Arguments.Count > 1 && e.Arguments[1] is ValueConstantToken arg2 ? arg2.GetBoolValue() : true;
                return new WhereTypesMethod(source, arg1.GetNodeTypeGuids(dm), includeDescendants);
            } else {
                throw new Exception("WhereTypes statement only accepts one or two parameter. ");
            }
        }
        if (name == "orderby") {
            var arg = e.Arguments[0];
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            if (arg is LambdaToken lambda) {
                if (lambda.Paramaters == null) throw new NullReferenceException();
                if (lambda.Paramaters.Count != 1) throw new Exception("Where statement only accepts a lambda expression with one parameter. ");
                var descending = e.Arguments.Count > 1 && (e.Arguments[1] + "").ToLower() == "true";
                LambdaExpression lambdaEx = ExpressionTreeBuilder.BuildLambda(lambda, dm);
                return new OrderByMethod(source, lambdaEx, descending);
            } else {
                throw new Exception("OrderBy statement only accept lambda expressions or strings as argument. ");
            }
        }
        if (name == "facets") {
            if (e.Subject == null) throw new NullReferenceException();
            foreach (var arg in e.Arguments) if (arg is not ValueConstantToken) throw new Exception("Only string arguments allowed in facet expression. ");
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            return new FacetMethod(source, e.Arguments.Cast<ValueConstantToken>().Select(a => a.GetStringValue()), dm);
        }
        if (name == "addfacet") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantToken arg) throw new Exception("Only string arguments allowed in facet expression. ");
            fc.AddFacet(arg.GetStringValue());
            return fc;
        }
        if (name == "addvaluefacet") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantToken arg) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments.Count == 1) {
                fc.AddValueFacet(arg.GetStringValue());
            } else {
                if (e.Arguments[1] is not ValueConstantToken arg2) throw new Exception("Only string arguments allowed in facet expression. ");
                fc.AddValueFacet(arg.GetStringValue(), arg2.GetStringValue());
            }
            return fc;
        }
        if (name == "addrangefacet") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantToken arg) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments.Count == 1) {
                fc.AddRangeFacet(arg.GetStringValue());
            } else {
                if (e.Arguments[1] is not ValueConstantToken arg2) throw new Exception("Only string arguments allowed in facet expression. ");
                if (e.Arguments[2] is not ValueConstantToken arg3) throw new Exception("Only string arguments allowed in facet expression. ");
                fc.AddRangeFacet(arg.GetStringValue(), arg2.GetStringValue(), arg3.GetStringValue());
            }
            return fc;
        }
        if (name == "setfacetvalue") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantToken arg1) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments[1] is not ValueConstantToken arg2) throw new Exception("Only string arguments allowed in facet expression. ");
            fc.SetFacetValue(arg1.GetStringValue(), arg2.GetStringValue());
            return fc;
        }
        if (name == "setfacetrangevalue") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            if (source is not FacetMethod fc) throw new Exception("Expected facet expression. ");
            if (e.Arguments[0] is not ValueConstantToken arg1) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments[1] is not ValueConstantToken arg2) throw new Exception("Only string arguments allowed in facet expression. ");
            if (e.Arguments[2] is not ValueConstantToken arg3) throw new Exception("Only string arguments allowed in facet expression. ");
            fc.SetFacetRangeValue(arg1.GetStringValue(), arg2.GetStringValue(), arg3.GetStringValue());
            return fc;
            throw new NotSupportedException("The method \"" + e + "\" is not supported. ");
        }
        if (name == "search" || name == "wheresearch") {
            if (e.Subject == null) throw new NullReferenceException();
            foreach (var arg in e.Arguments) if (arg is not ValueConstantToken) throw new Exception("Only string argument allowed in search expression. ");
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            if (e.Arguments.Count < 1) throw new Exception("Missing search parameter. ");
            var searchTextO = (ValueConstantToken)e.Arguments[0];
            double? semanticRatio = null;
            if (e.Arguments.Count > 1) {
                var semanticRatioO = (ValueConstantToken)e.Arguments[1];
                semanticRatio = semanticRatioO.GetDoubleOrNullValue();
            }
            float? minimumVectorSimilarity = null;
            if (e.Arguments.Count > 2) {
                var minimumVectorSimilarityO = (ValueConstantToken)e.Arguments[2];
                minimumVectorSimilarity = minimumVectorSimilarityO.GetFloatOrNullValue();
            }
            bool? orSearch = null;
            if (e.Arguments.Count > 3) {
                var orSearchO = (ValueConstantToken)e.Arguments[3];
                orSearch = orSearchO.GetBoolOrNullValue();
            }
            int? maxWordsEvaluated = null;
            if (e.Arguments.Count > 4) {
                var maxWordsEvaluatedO = (ValueConstantToken)e.Arguments[4];
                maxWordsEvaluated = maxWordsEvaluatedO.GetIntOrNullValue();
            }
            int? maxHitsEvaluated = null;
            if (e.Arguments.Count > 5) {
                var maxHitsEvaluatedO = (ValueConstantToken)e.Arguments[5];
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
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            if (e.Arguments.Count != 2) throw new Exception("Page statement only accepts two parameters. ");
            var p1 = ((ValueConstantToken)e.Arguments[0]).GetIntValue();
            var p2 = ((ValueConstantToken)e.Arguments[1]).GetIntValue();
            return new PageMethod(source, p1, p2);
        }
        if (name == "take") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            if (e.Arguments.Count != 1) throw new Exception("Take statement only accepts one parameter. ");
            var p1 = ((ValueConstantToken)e.Arguments[0]).GetIntValue();
            return new TakeMethod(source, p1);
        }
        if (name == "skip") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            if (e.Arguments.Count != 1) throw new Exception("Skip statement only accepts one parameter. ");
            var p1 = ((ValueConstantToken)e.Arguments[0]).GetIntValue();
            return new SkipMethod(source, p1);
        }
        if (name == "count") {
            if (e.Arguments.Count > 0) throw new Exception("Count does not accepts arguments. ");
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            var s = new CountMethod(source);
            return s;
        }
        if (name == "sum") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            LambdaExpression? lambdaEx = null;
            if (e.Arguments.Count > 0) {
                var arg = e.Arguments[0];
                if (arg is not LambdaToken lambda) throw new Exception("Sum statement only accept lambda expressions as argument. ");
                if (lambda.Paramaters == null) throw new NullReferenceException();
                if (lambda.Paramaters.Count != 1) throw new Exception("Sum statement only accepts a lambda expression with one parameter. ");
                lambdaEx = ExpressionTreeBuilder.BuildLambda(lambda, dm);
            }
            return new SumMethod(source, lambdaEx);
        }
        if (name == "any") {
            if (e.Subject == null) throw new NullReferenceException();
            var path = (e.Subject as VariableReferenceToken)?.Name;
            if (path == null) throw new NullReferenceException();
            return new RelationExpression(path, e.Arguments[0].ToString(), name);
        }
        if (name == "is" || name == "has") {
            if (e.Subject == null) throw new NullReferenceException();
            var path = (e.Subject as VariableReferenceToken)?.Name;
            if (path == null) throw new NullReferenceException();
            return new RelationExpression(path, e.Arguments[0].ToString(), name);
        }
        if (name == "wherein") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            List<string> branch = new();
            if (e.Arguments[0] is not ValueConstantToken prop) throw new Exception("Only string arguments allowed as property in relation expression. ");
            if (e.Arguments.Count > 1 && e.Arguments[1] is ValueConstantToken id) {
                var propId = prop.GetPropertyId(dm);
                return new WhereInMethod(source, propId, id.GetPropertyValues(dm, propId));
            } else {
                throw new Exception("Relates statement only accepts two parameters. ");
            }
        }
        if (name == "whereinids") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            List<string> branch = new();
            if (e.Arguments.Count > 0 && e.Arguments[0] is ValueConstantToken id) {
                return new WhereInIdsMethod(source, id.GetGuids());
            } else {
                throw new Exception("Relates statement only accepts one parameter. ");
            }
        }
        if (name == "relatesany") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            List<string> branch = new();
            if (e.Arguments[0] is not ValueConstantToken prop) throw new Exception("Only string arguments allowed as property in relation expression. ");
            if (e.Arguments.Count > 1 && e.Arguments[1] is ValueConstantToken id) {
                return new RelatesAnyMethod(source, dm, prop.GetStringValue(), id.GetStringValue());
            } else {
                throw new Exception("Relates statement only accepts two parameters. ");
            }
        }
        if (name == "relates") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            List<string> branch = new();
            if (e.Arguments[0] is not ValueConstantToken prop) throw new Exception("Only string arguments allowed as property in relation expression. ");
            if (e.Arguments.Count > 1 && e.Arguments[1] is ValueConstantToken id) {
                return new RelatesMethod(source, dm, prop.GetStringValue(), id.GetStringValue());
            } else {
                throw new Exception("Relates statement only accepts two parameters. ");
            }
        }
        if (name == "relatesnot") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            List<string> branch = new();
            if (e.Arguments[0] is not ValueConstantToken prop) throw new Exception("Only string arguments allowed as property in relation expression. ");
            if (e.Arguments.Count > 0 && e.Arguments[1] is ValueConstantToken id) {
                return new RelatesNotMethod(source, dm, prop.GetStringValue(), id.GetStringValue());
            } else {
                throw new Exception("Relates statement only accepts two parameters. ");
            }
        }
        if (name == "include") {
            if (e.Subject == null) throw new NullReferenceException();
            var source = ExpressionTreeBuilder.Build(e.Subject, dm);
            List<string> branch = new();
            if (e.Arguments[0] is not ValueConstantToken prop) throw new Exception("Only string arguments allowed as property in include expression. ");
            return new IncludeMethod(source, dm, prop.GetStringValue());
        }
        if (name == "inrange") {
            if (e.Subject == null) throw new NullReferenceException();
            var path = (e.Subject as VariableReferenceToken)?.Name;
            if (path == null) throw new NullReferenceException();
            return new RangeExpression(path, e.Arguments[0].ToString(), e.Arguments[1].ToString());
        }
        throw new NotSupportedException("The method \"" + e + "\" is not supported. ");
    }
}
