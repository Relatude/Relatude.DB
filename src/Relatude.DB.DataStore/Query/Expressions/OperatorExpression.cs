using System.Text;
namespace Relatude.DB.Query.Expressions;
public class OperatorExpression : IExpression {
    public List<Operator> Operators;
    public List<IExpression> Expressions;
    readonly bool _reorderEvaluationTree = false;
    public bool IsBooleanExpression {
        get {
            if (Operators.Count > 0) {
                return OperatorUtil.IsBoolean(Operators[0]);
            } else if (Expressions.Count == 1) {
                return Expressions[0] is OperatorExpression oe && oe.IsBooleanExpression;
            } else {
                throw new NotSupportedException();
            }
        }
    }
    protected OperatorExpression(IExpression expressions, bool reorderEvaluationTree) {
        Expressions = new() { expressions };
        Operators = new();
        _reorderEvaluationTree = reorderEvaluationTree;
        if (reorderEvaluationTree) buildEvaluationTreeFromOperatorPrecedence();
    }
    public OperatorExpression(List<IExpression> expressions, List<Operator> operators, bool reorderEvaluationTree) {
        Expressions = expressions;
        Operators = operators;
        _reorderEvaluationTree = reorderEvaluationTree;
        if (reorderEvaluationTree) buildEvaluationTreeFromOperatorPrecedence();
    }
    public object? Evaluate(IVariables vars) {
        object? result = Expressions[0].Evaluate(vars);
        try {
            for (var i = 0; i < Expressions.Count - 1; i++) {
                result = evaluate(result, Operators[i], Expressions[i + 1].Evaluate(vars));
            }
        } catch (NotSupportedException err) {
            throw new NotSupportedException("Cannot evaluate " + ToString() + ". " + err.Message);
        }
        return result;
    }
    void buildEvaluationTreeFromOperatorPrecedence() {
        // ensures 1+2*3 is evaluated as 1+(2*3) and not (1+2)*3
        var relevantPrecedencesInOrderOfEval = Operators.Select(o => OperatorUtil.Precedence(o)).Distinct().OrderBy(o => o).ToList();
        var expressions = Expressions;
        var operators = Operators;
        if (relevantPrecedencesInOrderOfEval.Count() < 2) return; // all same, so no tree..
        relevantPrecedencesInOrderOfEval.RemoveAt(relevantPrecedencesInOrderOfEval.Count - 1); // remove last
        foreach (var p in relevantPrecedencesInOrderOfEval) {
            var newExpList = new List<IExpression>();
            var newOpList = new List<Operator>();
            int i;
            for (i = 0; i < expressions.Count - 1; i++) {
                if (OperatorUtil.Precedence(operators[i]) == p) {
                    var subExpList = new List<IExpression>();
                    var subOpList = new List<Operator>();
                    while (i < expressions.Count - 1 && OperatorUtil.Precedence(operators[i]) == p) {
                        subExpList.Add(expressions[i]);
                        subOpList.Add(operators[i]);
                        i++;
                    }
                    subExpList.Add(expressions[i]);
                    var subExpression = new OperatorExpression(subExpList, subOpList, false);
                    newExpList.Add(subExpression);
                } else {
                    newExpList.Add(expressions[i]);
                }
                if (i < operators.Count) newOpList.Add(operators[i]);
            }
            if (i < expressions.Count) newExpList.Add(expressions[i]);
            expressions = newExpList;
            operators = newOpList;
        }
        Expressions = expressions;
        Operators = operators;
    }
    object evaluate(object? v1, Operator op, object? v2) {
        // room for optimization here.... should not need to be evaluated for every row
        if (v1 is int i1 && v2 is int i2) {
            return op switch {
                Operator.Equal => i1 == i2,
                Operator.NotEqual => i1 != i2,
                Operator.Greater => i1 > i2,
                Operator.Smaller => i1 < i2,
                Operator.GreaterOrEqual => i1 >= i2,
                Operator.SmallerOrEqual => i1 <= i2,
                Operator.Plus => i1 + i2,
                Operator.Minus => i1 - i2,
                Operator.Multiply => i1 * i2,
                Operator.Divide => i1 / i2,
                _ => throw operatorException(v1, op, v2),
            };
        }
        if (v1 is string s1 && v2 is string s2) {
            return op switch {
                Operator.Plus => s1 + s2,
                Operator.Equal => s1 == s2,
                Operator.NotEqual => s1 != s2,
                _ => throw operatorException(v1, op, v2),
            };
        }
        if (v1 is DateTime dt1 && v2 is DateTime dt2) {
            return op switch {
                Operator.Equal => dt1 == dt2,
                Operator.NotEqual => dt1 != dt2,
                Operator.Greater => dt1 > dt2,
                Operator.Smaller => dt1 < dt2,
                Operator.GreaterOrEqual => dt1 >= dt2,
                Operator.SmallerOrEqual => dt1 <= dt2,
                _ => throw operatorException(v1, op, v2),
            };
        }
        if (v1 is TimeSpan ts1 && v2 is TimeSpan ts2) {
            return op switch {
                Operator.Equal => ts1 == ts2,
                Operator.NotEqual => ts1 != ts2,
                Operator.Greater => ts1 > ts2,
                Operator.Smaller => ts1 < ts2,
                Operator.GreaterOrEqual => ts1 >= ts2,
                Operator.SmallerOrEqual => ts1 <= ts2,
                Operator.Plus => ts1 + ts2,
                Operator.Minus => ts1 - ts2,
                _ => throw operatorException(v1, op, v2),
            };
        }
        if (v1 is long l1 && v2 is long l2) {
            return op switch {
                Operator.Equal => l1 == l2,
                Operator.NotEqual => l1 != l2,
                Operator.Greater => l1 > l2,
                Operator.Smaller => l1 < l2,
                Operator.GreaterOrEqual => l1 >= l2,
                Operator.SmallerOrEqual => l1 <= l2,
                Operator.Plus => l1 + l2,
                Operator.Minus => l1 - l2,
                Operator.Multiply => l1 * l2,
                Operator.Divide => l1 / l2,
                _ => throw operatorException(v1, op, v2),
            };
        }
        if (v1 is double d1 && v2 is double d2) {
            return op switch {
                Operator.Equal => d1 == d2,
                Operator.NotEqual => d1 != d2,
                Operator.Greater => d1 > d2,
                Operator.Smaller => d1 < d2,
                Operator.GreaterOrEqual => d1 >= d2,
                Operator.SmallerOrEqual => d1 <= d2,
                Operator.Plus => d1 + d2,
                Operator.Minus => d1 - d2,
                Operator.Multiply => d1 * d2,
                Operator.Divide => d1 / d2,
                _ => throw operatorException(v1, op, v2),
            };
        }
        if (v1 is float f1 && v2 is float f2) {
            return op switch {
                Operator.Equal => f1 == f2,
                Operator.NotEqual => f1 != f2,
                Operator.Greater => f1 > f2,
                Operator.Smaller => f1 < f2,
                Operator.GreaterOrEqual => f1 >= f2,
                Operator.SmallerOrEqual => f1 <= f2,
                Operator.Plus => f1 + f2,
                Operator.Minus => f1 - f2,
                Operator.Multiply => f1 * f2,
                Operator.Divide => f1 / f2,
                _ => throw operatorException(v1, op, v2),
            };
        }
        if (v1 is decimal de1 && v2 is decimal de2) {
            return op switch {
                Operator.Equal => de1 == de2,
                Operator.NotEqual => de1 != de2,
                Operator.Greater => de1 > de2,
                Operator.Smaller => de1 < de2,
                Operator.GreaterOrEqual => de1 >= de2,
                Operator.SmallerOrEqual => de1 <= de2,
                Operator.Plus => de1 + de2,
                Operator.Minus => de1 - de2,
                Operator.Multiply => de1 * de2,
                Operator.Divide => de1 / de2,
                _ => throw operatorException(v1, op, v2),
            };
        }
        if (v1 is bool b1 && v2 is bool b2) {
            return op switch {
                Operator.And => b1 && b2,
                Operator.Or => b1 || b2,
                Operator.Equal => b1 == b2,
                Operator.NotEqual => b1 != b2,
                _ => throw operatorException(v1, op, v2),
            };
        }
        if (v1 is null && v2 is null) {
            return op switch {
                Operator.Equal => true,
                Operator.NotEqual => false,
                _ => throw operatorException(v1, op, v2),
            };
        }
        if (v1 is null || v2 is null) {
            return op switch {
                Operator.Equal => false,
                Operator.NotEqual => true,
                _ => throw operatorException(v1, op, v2),
            };
        }
        if (v1.GetType() != v2.GetType()) {
            OperatorUtil.CastToBestCommonTypeForComparison(v1, v2, out var o1, out var o2);
            return evaluate(o1, op, o2);
        }
        throw operatorException(v1, op, v2);
    }
    Exception operatorException(object? v1, Operator op, object? v2) {
        return new NotSupportedException("The operation " + v1 + "[" + (v1 == null ? "NULL" : v1.GetType().Name) + "] "
            + OperatorUtil.ToString(op) + " " + v2?.ToString() + "[" + (v2 == null ? "NULL" : v2.GetType().Name) + "]" + " is not implemented. ");
    }
    IExpression removeBracketsIfPossible() {
        if (Expressions.Count == 1) return Expressions[0];
        return this;
    }
    /// <summary>
    /// Tries to simplyfy expressions like (article) => article.Name == ("test" + "s243"),
    /// to (article) => article.Name == "tests243"
    /// This will enable indexes in the evaluation. 
    /// </summary>
    /// <returns></returns>
    public IExpression Simplify() {
        // room for improvement here. Could be pre evaluateing other things as well.
        // should be tested more aswell
        // pupose is to make the lambda expression as simple as possible to speed up query execution.
        // and make query execution recognize the lambda expression as a native expression and evaluate it using indexes
        // to be able to use indexes it must reduce operator expressions to a simple expression.
        // example: (article) => article.Name == ("test" + "s243"),
        // must be reduced to (article) => article.Name == "tests243"
        // otherwise it will not be recognized it as a native expression
        // and will be evaluated using a loop for every record ( like link Objects ).
        var n = 0;
        while (n <= Operators.Count) {
            var e1 = Expressions[n];
            if (e1 is OperatorExpression op1) {
                e1 = op1.Simplify();
                Expressions[n] = e1;
            }
            if (n >= Operators.Count) break;
            var e2 = Expressions[n + 1];
            if (e2 is OperatorExpression op2) {
                e2 = op2.Simplify();
                Expressions[n + 1] = e2;
            }
            if (e1 is ConstantExpression c1 && e2 is ConstantExpression c2) {
                var v1 = c1.Value;
                var v2 = c2.Value;
                var v = evaluate(v1, Operators[n], v2);
                var combined = toConstantExpression(v);
                Expressions.RemoveAt(n + 1);
                Operators.RemoveAt(n);
                Expressions[n] = combined;
            } else {
                n++;
            }
        }
        return this.removeBracketsIfPossible();
    }
    static ConstantExpression toConstantExpression(object v) {
        return new ConstantExpression(v);
    }
    override public string ToString() {
        StringBuilder sb = new();
        bool isSubExpression = !_reorderEvaluationTree; // only outer expression is reordered
        if (isSubExpression) sb.Append("(");
        var i = -1;
        while (++i < Expressions.Count - 1) {
            sb.Append(Expressions[i].ToString());
            sb.Append(' ');
            sb.Append(OperatorUtil.ToString(Operators[i]));
            sb.Append(' ');
        }
        sb.Append(Expressions[i].ToString());
        if (isSubExpression) sb.Append(")");
        return sb.ToString();
    }
}
