using System.Reflection;
namespace WAF.Query.ExpressionToString.ZSpitz.Extensions;
public static class ParameterInfoExtensions {
    public static bool HasAttribute<TAttribute>(this ParameterInfo pi, bool inherit = false) where TAttribute : Attribute =>
        pi.GetCustomAttributes(typeof(TAttribute), inherit).Any();
}
