using Relatude.DB.GraphQL.Language;
using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Execution;

/// <summary>Resolves a field node's arguments against the field definition, applying defaults.</summary>
internal static class Arguments {

    public static Dictionary<string, object?> Resolve(ExecutionContext ctx, GqlField fieldDef, FieldNode fieldNode) {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var argDef in fieldDef.Arguments) {
            Argument? provided = null;
            foreach (var a in fieldNode.Arguments) {
                if (a.Name == argDef.Name) { provided = a; break; }
            }
            if (provided != null) result[argDef.Name] = ValueResolver.Resolve(ctx, provided.Value, argDef.Type);
            else if (argDef.HasDefaultValue) result[argDef.Name] = argDef.DefaultValue;
            else result[argDef.Name] = null;
        }
        return result;
    }

    public static int? GetInt(Dictionary<string, object?> args, string name)
        => args.TryGetValue(name, out var v) && v != null ? Convert.ToInt32(v) : null;

    public static string? GetString(Dictionary<string, object?> args, string name)
        => args.TryGetValue(name, out var v) ? v as string : null;

    public static bool GetBool(Dictionary<string, object?> args, string name)
        => args.TryGetValue(name, out var v) && v is true;
}
