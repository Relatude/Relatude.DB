using System.Text;

namespace Relatude.DB.GraphQL.Schema;

/// <summary>Prints a <see cref="GqlSchema"/> as GraphQL SDL.</summary>
internal static class SdlWriter {
    public static string Write(GqlSchema schema) {
        var sb = new StringBuilder();
        writeType(sb, schema.QueryType);
        var rest = schema.Types.Values
            .Where(t => t != schema.QueryType && t is not GqlScalarType { IsBuiltIn: true })
            .OrderBy(rank)
            .ThenBy(t => t.Name, StringComparer.Ordinal);
        foreach (var t in rest) writeType(sb, t);
        return sb.ToString();
    }

    static int rank(GqlNamedType t) => t switch {
        GqlScalarType => 0,
        GqlInterfaceType { Name: "Node" } => 1,
        GqlInterfaceType => 2,
        GqlObjectType => 3,
        GqlEnumType => 4,
        GqlInputObjectType => 5,
        _ => 6,
    };

    static void writeType(StringBuilder sb, GqlNamedType t) {
        if (sb.Length > 0) sb.AppendLine();
        switch (t) {
            case GqlScalarType s:
                description(sb, s.Description, "");
                sb.AppendLine($"scalar {s.Name}");
                break;
            case GqlEnumType e:
                description(sb, e.Description, "");
                sb.AppendLine($"enum {e.Name} {{");
                foreach (var v in e.Values) sb.AppendLine($"  {v.Name}");
                sb.AppendLine("}");
                break;
            case GqlInterfaceType i:
                description(sb, i.Description, "");
                sb.Append($"interface {i.Name}");
                implementsClause(sb, i.Interfaces);
                sb.AppendLine(" {");
                foreach (var f in i.Fields) writeField(sb, f);
                sb.AppendLine("}");
                break;
            case GqlObjectType o:
                description(sb, o.Description, "");
                sb.Append($"type {o.Name}");
                implementsClause(sb, o.Interfaces);
                sb.AppendLine(" {");
                foreach (var f in o.Fields) writeField(sb, f);
                sb.AppendLine("}");
                break;
            case GqlInputObjectType input:
                description(sb, input.Description, "");
                sb.AppendLine($"input {input.Name} {{");
                foreach (var f in input.InputFields) {
                    description(sb, f.Description, "  ");
                    sb.AppendLine($"  {f.Name}: {f.Type.ToTypeReference()}");
                }
                sb.AppendLine("}");
                break;
        }
    }

    static void implementsClause(StringBuilder sb, List<GqlInterfaceType> interfaces) {
        if (interfaces.Count == 0) return;
        sb.Append(" implements ");
        sb.Append(string.Join(" & ", interfaces.Select(i => i.Name)));
    }

    static void writeField(StringBuilder sb, GqlField f) {
        description(sb, f.Description, "  ");
        sb.Append($"  {f.Name}");
        if (f.Arguments.Count > 0) {
            sb.Append('(');
            for (var i = 0; i < f.Arguments.Count; i++) {
                var a = f.Arguments[i];
                if (i > 0) sb.Append(", ");
                sb.Append($"{a.Name}: {a.Type.ToTypeReference()}");
                if (a.HasDefaultValue) sb.Append($" = {literal(a.DefaultValue)}");
            }
            sb.Append(')');
        }
        sb.AppendLine($": {f.Type.ToTypeReference()}");
    }

    static string literal(object? value) => value switch {
        null => "null",
        bool b => b ? "true" : "false",
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
    };

    static void description(StringBuilder sb, string? text, string indent) {
        if (string.IsNullOrEmpty(text)) return;
        sb.AppendLine($"{indent}\"\"\"{text.Replace("\"\"\"", "\\\"\"\"")}\"\"\"");
    }
}
