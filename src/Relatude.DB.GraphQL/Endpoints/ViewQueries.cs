using Relatude.DB.Datamodels;
using Relatude.DB.Query.Parsing.Expressions;
using Relatude.DB.Query.Parsing.Tokens;

namespace Relatude.DB.GraphQL.Endpoints;

/// <summary>What a view's query text amounts to: the node type it starts from, or why it cannot be used.</summary>
public sealed class ViewQueryInfo {
    public NodeTypeModel? NodeType { get; init; }
    public string? Error { get; init; }
}

/// <summary>Checks the query text of a view without running it.</summary>
public static class ViewQueries {
    // methods that keep the result a node collection the executor can page, order and include into
    static readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase) {
        "Where", "WhereSearch", "WhereInIds", "WhereIn", "WhereTypes", "Relates", "RelatesNot", "RelatesAny",
        "OrderBy", "WhereCulture", "WhereCultureFallback", "WhereHidden", "Traverse",
    };

    public static ViewQueryInfo Inspect(string? query, Datamodel dm) {
        if (string.IsNullOrWhiteSpace(query)) return new ViewQueryInfo { Error = "the query is empty." };
        TokenBase tokens;
        try {
            tokens = TokenParser.Parse(query, []);
        } catch (Exception ex) {
            return new ViewQueryInfo { Error = "the query does not parse: " + ex.Message.Trim() };
        }
        var t = tokens;
        var methods = new List<string>();
        while (t is MethodCallToken m) {
            methods.Add(m.Name);
            if (m.Subject == null) break;
            t = m.Subject;
        }
        if (t is not VariableReferenceToken root) return new ViewQueryInfo { Error = "the query must start with a node type name, e.g. Article.Where(a => ...)." };
        var typeName = root.Name;
        if (typeName.Contains('.')) return new ViewQueryInfo { Error = $"\"{typeName}\" is not a type name; use the type's code name without namespace." };
        var type = resolveType(dm, typeName);
        if (type == null) return new ViewQueryInfo { Error = $"the type \"{typeName}\" is not in the datamodel." };
        foreach (var name in methods) {
            if (!_allowed.Contains(name)) return new ViewQueryInfo { Error = $"\"{name}\" cannot be used in a view; the endpoint adds paging, ordering and includes itself. Allowed: {string.Join(", ", _allowed)}." };
        }
        try {
            ExpressionTreeBuilder.Build(tokens, dm);
        } catch (Exception ex) {
            return new ViewQueryInfo { Error = "the query is not valid: " + ex.Message.Trim() };
        }
        return new ViewQueryInfo { NodeType = type };
    }

    static NodeTypeModel? resolveType(Datamodel dm, string name) {
        dm.EnsureInitalization();
        if (!dm.NodeTypesByShortName.TryGetValue(name, out var candidates) || candidates.Length == 0) return null;
        // the store resolves the root variable case-sensitively, so prefer the exact spelling
        return candidates.FirstOrDefault(c => c.CodeName == name) ?? (candidates.Length == 1 ? candidates[0] : null);
    }
}
