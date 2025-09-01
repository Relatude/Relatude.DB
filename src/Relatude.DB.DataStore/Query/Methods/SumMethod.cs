using System.Linq;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Methods;
public class SumMethod : IExpression {
    readonly IExpression _source;
    readonly LambdaExpression? _lambda;
    public SumMethod(IExpression source, LambdaExpression? lambdaEx) {
        _source = source;
        _lambda = lambdaEx;
    }
    public object Evaluate(IVariables vars) {
        var values = _source.Evaluate(vars);
        if (values is not ICollectionData data) throw new Exception("Count is only supported on collections. ");
        if (_lambda == null) {
            var firstValue = data.Values.FirstOrDefault();
            if (firstValue == null) return 0;
            if (firstValue is int) return data.Values.Cast<int>().Sum();
            if (firstValue is double) return data.Values.Cast<double>().Sum();
            if (firstValue is float) return data.Values.Cast<float>().Sum();
            if (firstValue is decimal) return data.Values.Cast<decimal>().Sum();
            if (firstValue is long) return data.Values.Cast<long>().Sum();
            if (firstValue is byte) return data.Values.Cast<int>().Sum();
            throw new Exception("Sum is not supported for type " + firstValue.GetType() + ". Use a lambda expression to specify the property to sum. ");
        } else {
            var rowVars = vars.CreateScope();
            if (_lambda.Parameters.Count != 1) throw new Exception("Lambda expression does only support one paramater. ");
            var lambdaParamaterName = _lambda.Parameters[0];
            rowVars.Declare(lambdaParamaterName);
            if (data.Count == 0) return 0;
            var firstRow = data.Values.First();
            rowVars.Set(lambdaParamaterName, firstRow);
            var firstValue = _lambda.Evaluate(rowVars);
            if (firstValue is int) {
                int sum = 0;
                foreach (var o in data.Values) {
                    rowVars.Set(lambdaParamaterName, o);
                    sum += (int)_lambda.Evaluate(rowVars);
                }
                return sum;
            }
            if (firstValue is double) {
                double sum = 0;
                foreach (var o in data.Values) {
                    rowVars.Set(lambdaParamaterName, o);
                    sum += (double)_lambda.Evaluate(rowVars);
                }
                return sum;
            }
            if (firstValue is float) {
                float sum = 0;
                foreach (var o in data.Values) {
                    rowVars.Set(lambdaParamaterName, o);
                    sum += (float)_lambda.Evaluate(rowVars);
                }
                return sum;
            }
            if (firstValue is decimal) {
                decimal sum = 0;
                foreach (var o in data.Values) {
                    rowVars.Set(lambdaParamaterName, o);
                    sum += (decimal)_lambda.Evaluate(rowVars);
                }
                return sum;
            }
            throw new Exception("Sum is not supported for type " + firstValue.GetType());
        }
        throw new NotImplementedException();
    }
    override public string ToString() => _source + ".Sum(" + (_lambda?.ToString()) + ")";
}
