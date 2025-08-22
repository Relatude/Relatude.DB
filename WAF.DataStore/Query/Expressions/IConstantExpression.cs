using System.Globalization;

namespace WAF.Query.Expressions;
public interface IConstantExpression : IExpression {
    object GetValue();
}
public class BooleanConstantExpression : IConstantExpression {
    public readonly bool Value;
    public BooleanConstantExpression(bool value) {
        Value = value;
    }
    public override string ToString() => Value.ToString();
    public object Evaluate(IVariables vars) => Value;
    public object GetValue() => Value;
}
public class IntegerConstantExpression(int value) : IConstantExpression {
    public readonly int Value = value;
    public override string ToString() => Value.ToString();
    public object Evaluate(IVariables vars) => Value;
    public object GetValue() => Value;
}
public class LongConstantExpression(long value) : IConstantExpression {
    public readonly long Value = value;
    public override string ToString() => Value.ToString();
    public object Evaluate(IVariables vars) => Value;
    public object GetValue() => Value;
}
public class DecimalConstantExpression(decimal value) : IConstantExpression {
    public readonly decimal Value = value;
    public override string ToString() => Value.ToString();
    public object Evaluate(IVariables vars) => Value;
    public object GetValue() => Value;
}
public class DoubleConstantExpression(double value) : IConstantExpression {
    public readonly double Value = value;
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
    public object Evaluate(IVariables vars) => Value;
    public object GetValue() => Value;
}
public class NullConstantExpression : IConstantExpression {
    public NullConstantExpression() { }
    public override string ToString() => "null";
    public object Evaluate(IVariables vars) => null!;
    public object GetValue() => null!;
}
public class StringConstantExpression : IConstantExpression {
    public readonly string Value;
    public StringConstantExpression(string value) {
        Value = value;
    }
    public override string ToString() => "\"" + Value + "\"";
    public object Evaluate(IVariables vars) => Value;
    public object GetValue() => Value;
}
