using GraphQLParser.AST;
using Relatude.DB.Datamodels;
using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Execution;

/// <summary>
/// Walks a selection set and turns every selected relation/reference field into a
/// Guid-based Include path ("propGuid|top.propGuid..."), so the whole GraphQL tree
/// is fetched in one store query (no N+1).
/// </summary>
internal static class IncludePlanner {

    sealed class Branch {
        public required Guid PropertyId;
        public int? Top;
        public bool Unbounded; // any occurrence without a top cap → no cap
        public Dictionary<Guid, Branch> Children = [];
    }

    /// <summary>Union of relation paths across all type conditions in the selection.</summary>
    public static List<string> Plan(ExecutionContext ctx, GqlNamedType declaredType, IEnumerable<GraphQLSelectionSet> sets) {
        var roots = new Dictionary<Guid, Branch>();
        walk(ctx, declaredType as IGqlCompositeType, sets, roots, 1);
        var paths = new List<string>();
        foreach (var b in roots.Values) emit(b, "", paths);
        return paths;
    }

    static void walk(ExecutionContext ctx, IGqlCompositeType? composite, IEnumerable<GraphQLSelectionSet> sets, Dictionary<Guid, Branch> branches, int depth) {
        if (composite == null) return;
        foreach (var set in sets) walkSet(ctx, composite, set, branches, depth);
    }

    static void walkSet(ExecutionContext ctx, IGqlCompositeType composite, GraphQLSelectionSet set, Dictionary<Guid, Branch> branches, int depth) {
        foreach (var sel in set.Selections) {
            switch (sel) {
                case GraphQLField f: {
                        if (!DocumentWalker.DirectivesPass(ctx, f.Directives)) continue;
                        var name = f.Name.StringValue;
                        if (name.StartsWith("__", StringComparison.Ordinal)) continue;
                        if (!composite.TryGetField(name, out var fieldDef)) continue; // belongs to a sibling type condition
                        if (fieldDef.Source is not (FieldSource.RelationOne or FieldSource.RelationMany or FieldSource.ReferenceOne or FieldSource.ReferenceMany)) continue;
                        if (depth > ctx.Options.MaxIncludeDepth) {
                            throw ctx.RequestError($"The selection exceeds the maximum relation depth of {ctx.Options.MaxIncludeDepth} (at field \"{name}\").", f);
                        }
                        var propId = fieldDef.Property!.Id;
                        if (!branches.TryGetValue(propId, out var branch)) {
                            branch = new Branch { PropertyId = propId };
                            branches.Add(propId, branch);
                        }
                        mergeTop(ctx, branch, fieldDef, f);
                        if (f.SelectionSet != null) {
                            var child = childComposite(ctx, fieldDef.TargetNodeType!);
                            walk(ctx, child, [f.SelectionSet], branch.Children, depth + 1);
                        }
                        break;
                    }
                case GraphQLInlineFragment inf: {
                        if (!DocumentWalker.DirectivesPass(ctx, inf.Directives)) continue;
                        var target = composite;
                        var condition = inf.TypeCondition?.Type.Name.StringValue;
                        if (condition != null && ctx.Schema.TryGetType(condition, out var t) && t is IGqlCompositeType c) target = c;
                        if (inf.SelectionSet != null) walkSet(ctx, target, inf.SelectionSet, branches, depth);
                        break;
                    }
                case GraphQLFragmentSpread sp: {
                        if (!DocumentWalker.DirectivesPass(ctx, sp.Directives)) continue;
                        if (!ctx.Fragments.TryGetValue(sp.FragmentName.Name.StringValue, out var frag)) continue;
                        var target = composite;
                        var condition = frag.TypeCondition.Type.Name.StringValue;
                        if (ctx.Schema.TryGetType(condition, out var t) && t is IGqlCompositeType c) target = c;
                        if (frag.SelectionSet != null) walkSet(ctx, target, frag.SelectionSet, branches, depth);
                        break;
                    }
            }
        }
    }

    static void mergeTop(ExecutionContext ctx, Branch branch, GqlField fieldDef, GraphQLField f) {
        if (fieldDef.Source is FieldSource.RelationOne or FieldSource.ReferenceOne) { branch.Unbounded = true; return; }
        int? top = null;
        var topArgDef = fieldDef.GetArgument("top");
        if (topArgDef != null) {
            var args = Arguments.Resolve(ctx, fieldDef, f);
            top = Arguments.GetInt(args, "top");
            if (top is < 0) top = null;
        }
        if (top == null) branch.Unbounded = true;
        else if (!branch.Unbounded) branch.Top = Math.Max(branch.Top ?? 0, top.Value);
    }

    static IGqlCompositeType? childComposite(ExecutionContext ctx, NodeTypeModel target) {
        if (target.Id == NodeConstants.BaseNodeTypeId) return ctx.Schema.NodeInterface;
        return ctx.Schema.ReferenceTypesByNodeTypeId.TryGetValue(target.Id, out var t) ? t as IGqlCompositeType : null;
    }

    static void emit(Branch b, string prefix, List<string> paths) {
        var segment = b.PropertyId.ToString();
        if (!b.Unbounded && b.Top.HasValue) segment += "|" + b.Top.Value;
        var path = prefix.Length == 0 ? segment : prefix + "." + segment;
        if (b.Children.Count == 0) paths.Add(path);
        else foreach (var c in b.Children.Values) emit(c, path, paths);
    }
}
