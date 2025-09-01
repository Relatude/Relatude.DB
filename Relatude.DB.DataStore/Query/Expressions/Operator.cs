namespace Relatude.DB.Query.Expressions;
public enum Operator {
    And,
    Or,
    Equal,
    NotEqual,
    Greater,
    Smaller,
    GreaterOrEqual,
    SmallerOrEqual,
    Search,
    Plus,
    Minus,
    Multiply,
    Divide,
}
public static class OperatorUtil {
    public static string ToString(Operator o) {
        return o switch {
            Operator.And => "&&",
            Operator.Or => "||",
            Operator.Equal => "==",
            Operator.NotEqual => "!=",
            Operator.Greater => ">",
            Operator.Smaller => "<",
            Operator.GreaterOrEqual => ">=",
            Operator.SmallerOrEqual => "<=",
            Operator.Plus => "+",
            Operator.Minus => "-",
            Operator.Multiply => "*",
            Operator.Divide => "/",
            _ => throw new Exception("Unknown operator: " + o + ". "),
        };
    }
    public static Operator Parse(string o) {
        return o switch {
            "&&" => Operator.And,
            "||" => Operator.Or,
            "==" => Operator.Equal,
            "!=" => Operator.NotEqual,
            ">" => Operator.Greater,
            "<" => Operator.Smaller,
            ">=" => Operator.GreaterOrEqual,
            "<=" => Operator.SmallerOrEqual,
            "+" => Operator.Plus,
            "-" => Operator.Minus,
            "*" => Operator.Multiply,
            "/" => Operator.Divide,
            _ => throw new Exception("Unknown operator: " + o + ". "),
        };
    }
    public static int Precedence(Operator o) {
        return o switch {
            Operator.And => 10,
            Operator.Or => 11,
            Operator.Equal => 9,
            Operator.NotEqual => 8,
            Operator.Greater => 7,
            Operator.Smaller => 6,
            Operator.GreaterOrEqual => 5,
            Operator.SmallerOrEqual => 4,
            Operator.Plus => 2,
            Operator.Minus => 2,
            Operator.Multiply => 1,
            Operator.Divide => 1,
            //Operator.And => 4,
            //Operator.Or => 4,
            //Operator.Equal => 3,
            //Operator.NotEqual => 3,
            //Operator.Greater => 3,
            //Operator.Smaller => 3,
            //Operator.GreaterOrEqual => 3,
            //Operator.SmallerOrEqual => 3,
            //Operator.Plus => 2,
            //Operator.Minus => 2,
            //Operator.Multiply => 1,
            //Operator.Divide => 1,
            _ => throw new Exception("Unknown operator: " + o + ". "),
        };
    }
    public static bool IsBoolean(Operator o) {
        return o switch {
            Operator.And 
            or Operator.Or 
            or Operator.Equal 
            or Operator.NotEqual 
            or Operator.Greater 
            or Operator.Smaller 
            or Operator.GreaterOrEqual 
            or Operator.SmallerOrEqual => true,
            Operator.Plus
            or Operator.Minus 
            or Operator.Multiply 
            or Operator.Divide => false,
            _ => throw new Exception("Unknown operator: " + o + ". "),
        };
    }
    public static void CastToBestCommonTypeForComparison(object i1, object i2, out object o1, out object o2) {
        // assumes that i1 and i2 are of different types
        if (i1 is string || i2 is string) {
            o1 = i1 + string.Empty;
            o2 = i2 + string.Empty;
        } else if (i1 is double || i2 is double) {
            o1 = Convert.ToDouble(i1);
            o2 = Convert.ToDouble(i2);
        } else if (i1 is decimal || i2 is decimal) {
            o1 = Convert.ToDecimal(i1);
            o2 = Convert.ToDecimal(i2);
        } else if (i1 is float || i2 is float) {
            o1 = Convert.ToDouble(i1);
            o2 = Convert.ToDouble(i2);
        } else if (i1 is DateTime || i2 is DateTime) {
            o1 = i1 is long l1 ? new DateTime(l1, DateTimeKind.Utc) : Convert.ToDateTime(i1);
            o2 = i2 is long l2 ? new DateTime(l2, DateTimeKind.Utc) : Convert.ToDateTime(i2);
        } else if (i1 is long || i2 is long) {
            o1 = Convert.ToInt64(i1);
            o2 = Convert.ToInt64(i2);
        } else if (i1 is int || i2 is int) {
            o1 = Convert.ToInt32(i1);
            o2 = Convert.ToInt32(i2);
        } else if (i1 is byte || i2 is byte) {
            o1 = Convert.ToByte(i1);
            o2 = Convert.ToByte(i2);
        } else if (i1 is TimeSpan || i2 is TimeSpan) {
            o1 = (TimeSpan)i1;
            o2 = (TimeSpan)i2;
        } else if (i1 == null || i2 == null) {
            throw new Exception("Cannot compare null with non-null. ");
        } else {
            throw new Exception("Incompatible types. Type " + i1.GetType().Name + " and " + i2.GetType().Name + " comparison is not supported. ");
        }
    }
}

