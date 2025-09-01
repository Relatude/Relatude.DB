namespace Relatude.DB.DataStores.Indexes {
    public enum IndexOperator {
        Equal,
        NotEqual,
        Greater,
        Smaller,
        GreaterOrEqual,
        SmallerOrEqual,
    }
    public static class IndexOperatorUtil {
        public static string ToString(string propName, IndexOperator op, string value) {
            return op switch {
                IndexOperator.Equal => propName + "=" + value,
                IndexOperator.NotEqual => propName + "!=" + value,
                IndexOperator.Greater => propName + ">" + value,
                IndexOperator.Smaller => propName + "<" + value,
                IndexOperator.GreaterOrEqual => propName + ">=" + value,
                IndexOperator.SmallerOrEqual => propName + "<=" + value,
                _ => throw new Exception("Unknown operator: " + op + ". "),
            };
        }
        public static IndexOperator Parse(string op) {
            return op.ToLower() switch {
                "=" => IndexOperator.Equal,
                "!=" => IndexOperator.NotEqual,
                ">" => IndexOperator.Greater,
                "<" => IndexOperator.Smaller,
                ">=" => IndexOperator.GreaterOrEqual,
                "<=" => IndexOperator.SmallerOrEqual,
                _ => throw new Exception("Unknown operator: " + op + ". "),
            };
        }
    }
}
