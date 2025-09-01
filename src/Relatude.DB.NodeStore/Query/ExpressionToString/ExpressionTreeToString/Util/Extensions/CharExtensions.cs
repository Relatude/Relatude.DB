using System.Text;

namespace Relatude.DB.Query.ExpressionToString.ExpressionTreeToString.Util.Extensions {
    public static class CharExtensions {
        public static void AppendTo(this char c, StringBuilder sb) => sb.Append(c);
    }
}
