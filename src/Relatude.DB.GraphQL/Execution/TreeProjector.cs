using GraphQLParser.AST;

namespace Relatude.DB.GraphQL.Execution;

/// <summary>
/// Projects a selection over a plain object tree (dictionaries with "__typename", lists, scalars).
/// Used for introspection data. Missing keys resolve to null (lenient by design).
/// </summary>
internal static class TreeProjector {

    public static object? Project(ExecutionContext ctx, object? value, IEnumerable<GraphQLSelectionSet> sets) {
        switch (value) {
            case null:
                return null;
            case List<object?> list:
                return list.Select(item => Project(ctx, item, sets)).ToList();
            case Dictionary<string, object?> dict: {
                    var typeName = dict.TryGetValue("__typename", out var tn) ? tn as string : null;
                    var collected = DocumentWalker.CollectFields(ctx, name => name == typeName, sets);
                    var result = new Dictionary<string, object?>(collected.Count);
                    foreach (var cf in collected) {
                        var fieldName = cf.First.Name.StringValue;
                        if (fieldName == "__typename") { result[cf.Key] = typeName; continue; }
                        dict.TryGetValue(fieldName, out var child);
                        var hasSelection = cf.Fields.Any(f => f.SelectionSet != null);
                        if (hasSelection) {
                            result[cf.Key] = Project(ctx, child, cf.SelectionSets);
                        } else {
                            result[cf.Key] = child switch {
                                Dictionary<string, object?> => null, // composite selected without a subselection
                                List<object?> l when l.Any(i => i is Dictionary<string, object?>) => null,
                                _ => child,
                            };
                        }
                    }
                    return result;
                }
            default:
                return value;
        }
    }
}
