using System.Globalization;
using Relatude.DB.Common;
using Relatude.DB.Query.Data;

namespace Relatude.DB.Query.Expressions;

/// <summary>
/// x.Location.IsWithin(center, meters): true when the coordinate lies within the given
/// great-circle distance of center. Index-accelerated for indexed GeoCoordinate properties
/// (see the native expression in the local store); evaluates per row otherwise.
/// </summary>
public class GeoWithinExpression : IExpression {
    public readonly VariableReferenceExpression SourceObject;
    public readonly string PropertyName;
    public readonly GeoCoordinate Center;
    public readonly double Meters;
    readonly PropertyReferenceExpression _propertyRef;
    public GeoWithinExpression(string propertyPath, object? center, object? meters) {
        var parts = propertyPath.Split('.');
        SourceObject = new VariableReferenceExpression(parts[0]);
        PropertyName = parts.Skip(1).Single();
        _propertyRef = new PropertyReferenceExpression(SourceObject, PropertyName);
        Center = GeoExpressionUtil.ToCoordinate(center, "IsWithin");
        Meters = GeoExpressionUtil.ToMeters(meters, "IsWithin");
    }
    public object Evaluate(IVariables vars) { // row evaluation (non-indexed fallback)
        var v = _propertyRef.Evaluate(vars);
        if (v is GeoCoordinate g) return g.IsWithin(Center, Meters);
        return false;
    }
    public override string ToString() => SourceObject + "." + PropertyName + ".IsWithin(\"" + Center + "\", " + Meters.ToString(CultureInfo.InvariantCulture) + ")";
}

/// <summary>
/// x.Location.DistanceTo(center): the great-circle distance in meters, infinite for empty
/// coordinates (so they sort last in an ascending OrderBy). Always evaluated per row.
/// </summary>
public class GeoDistanceExpression : IExpression {
    public readonly VariableReferenceExpression SourceObject;
    public readonly string PropertyName;
    public readonly GeoCoordinate Center;
    readonly PropertyReferenceExpression _propertyRef;
    public GeoDistanceExpression(string propertyPath, object? center) {
        var parts = propertyPath.Split('.');
        SourceObject = new VariableReferenceExpression(parts[0]);
        PropertyName = parts.Skip(1).Single();
        _propertyRef = new PropertyReferenceExpression(SourceObject, PropertyName);
        Center = GeoExpressionUtil.ToCoordinate(center, "DistanceTo");
    }
    public object Evaluate(IVariables vars) {
        var v = _propertyRef.Evaluate(vars);
        if (v is GeoCoordinate g) return g.DistanceTo(Center);
        return double.PositiveInfinity;
    }
    public override string ToString() => SourceObject + "." + PropertyName + ".DistanceTo(\"" + Center + "\")";
}

static class GeoExpressionUtil {
    public static GeoCoordinate ToCoordinate(object? value, string method) {
        if (value is GeoCoordinate g) return g;
        if (value is string s && GeoCoordinate.TryParse(s, out var parsed)) return parsed;
        throw new Exception(method + " expects a GeoCoordinate or a \"latitude, longitude\" string, got: " + (value ?? "null") + ". ");
    }
    public static double ToMeters(object? value, string method) {
        if (value is double d) return d;
        if (value is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        if (value is IConvertible c) return c.ToDouble(CultureInfo.InvariantCulture);
        throw new Exception(method + " expects the distance in meters as a number, got: " + (value ?? "null") + ". ");
    }
}
