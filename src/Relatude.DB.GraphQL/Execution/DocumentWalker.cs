using GraphQLParser.AST;
using Relatude.DB.GraphQL.Schema;

namespace Relatude.DB.GraphQL.Execution;

/// <summary>A response key with the (possibly merged) field selections that produce it.</summary>
internal sealed class CollectedField {
    public required string Key { get; init; }
    public required GraphQLField First { get; init; }
    public List<GraphQLField> Fields { get; } = [];
    /// <summary>Selection sets of all merged occurrences (null entries filtered).</summary>
    public IEnumerable<GraphQLSelectionSet> SelectionSets {
        get {
            foreach (var f in Fields) if (f.SelectionSet != null) yield return f.SelectionSet;
        }
    }
}

/// <summary>Fragment handling, field collection, directives, depth checks and document validation.</summary>
internal static class DocumentWalker {

    // ---- fragment collection and safety ----

    public static Dictionary<string, GraphQLFragmentDefinition> CollectFragments(GraphQLDocument doc) {
        var fragments = new Dictionary<string, GraphQLFragmentDefinition>(StringComparer.Ordinal);
        foreach (var def in doc.Definitions) {
            if (def is GraphQLFragmentDefinition f) fragments[f.FragmentName.Name.StringValue] = f;
        }
        return fragments;
    }

    public static void EnsureNoFragmentCycles(ExecutionContext ctx) {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in ctx.Fragments.Keys) visit(name);
        void visit(string name) {
            if (done.Contains(name)) return;
            if (!visiting.Add(name)) throw ctx.RequestError($"Fragment cycle detected involving \"{name}\".");
            if (ctx.Fragments.TryGetValue(name, out var frag) && frag.SelectionSet != null) {
                foreach (var spread in spreadsIn(frag.SelectionSet)) visit(spread);
            }
            visiting.Remove(name);
            done.Add(name);
        }
        IEnumerable<string> spreadsIn(GraphQLSelectionSet set) {
            foreach (var s in set.Selections) {
                switch (s) {
                    case GraphQLFragmentSpread sp: yield return sp.FragmentName.Name.StringValue; break;
                    case GraphQLField f when f.SelectionSet != null: foreach (var n in spreadsIn(f.SelectionSet)) yield return n; break;
                    case GraphQLInlineFragment inf when inf.SelectionSet != null: foreach (var n in spreadsIn(inf.SelectionSet)) yield return n; break;
                }
            }
        }
    }

    /// <summary>Max response depth of the operation with fragment spreads expanded. Call after cycle check.</summary>
    public static int MaxDepth(ExecutionContext ctx, GraphQLSelectionSet set) {
        var fragmentDepths = new Dictionary<string, int>(StringComparer.Ordinal);
        return depthOf(set);
        int depthOf(GraphQLSelectionSet s) {
            var max = 0;
            foreach (var sel in s.Selections) {
                var d = sel switch {
                    GraphQLField f => 1 + (f.SelectionSet == null ? 0 : depthOf(f.SelectionSet)),
                    GraphQLInlineFragment inf => inf.SelectionSet == null ? 0 : depthOf(inf.SelectionSet),
                    GraphQLFragmentSpread sp => fragmentDepth(sp.FragmentName.Name.StringValue),
                    _ => 0,
                };
                if (d > max) max = d;
            }
            return max;
        }
        int fragmentDepth(string name) {
            if (fragmentDepths.TryGetValue(name, out var d)) return d;
            d = ctx.Fragments.TryGetValue(name, out var frag) && frag.SelectionSet != null ? depthOf(frag.SelectionSet) : 0;
            fragmentDepths[name] = d;
            return d;
        }
    }

    // ---- field collection (fragment flattening + directives) ----

    /// <summary>
    /// Flattens selection sets for a runtime type into an ordered list of response keys,
    /// resolving named/inline fragments via <paramref name="typeConditionMatches"/> and applying @skip/@include.
    /// </summary>
    public static List<CollectedField> CollectFields(ExecutionContext ctx, Func<string, bool> typeConditionMatches, IEnumerable<GraphQLSelectionSet> sets) {
        var ordered = new List<CollectedField>();
        var byKey = new Dictionary<string, CollectedField>(StringComparer.Ordinal);
        foreach (var set in sets) collect(set);
        return ordered;

        void collect(GraphQLSelectionSet set) {
            foreach (var sel in set.Selections) {
                switch (sel) {
                    case GraphQLField f: {
                            if (!DirectivesPass(ctx, f.Directives)) continue;
                            var key = f.Alias?.Name.StringValue ?? f.Name.StringValue;
                            if (!byKey.TryGetValue(key, out var cf)) {
                                cf = new CollectedField { Key = key, First = f };
                                byKey.Add(key, cf);
                                ordered.Add(cf);
                            }
                            cf.Fields.Add(f);
                            break;
                        }
                    case GraphQLInlineFragment inf: {
                            if (!DirectivesPass(ctx, inf.Directives)) continue;
                            var condition = inf.TypeCondition?.Type.Name.StringValue;
                            if (condition != null && !typeConditionMatches(condition)) continue;
                            if (inf.SelectionSet != null) collect(inf.SelectionSet);
                            break;
                        }
                    case GraphQLFragmentSpread sp: {
                            if (!DirectivesPass(ctx, sp.Directives)) continue;
                            if (!ctx.Fragments.TryGetValue(sp.FragmentName.Name.StringValue, out var frag)) continue; // validation reports this
                            if (!typeConditionMatches(frag.TypeCondition.Type.Name.StringValue)) continue;
                            if (frag.SelectionSet != null) collect(frag.SelectionSet);
                            break;
                        }
                }
            }
        }
    }

    /// <summary>Evaluates @skip / @include. Unknown directives are ignored.</summary>
    public static bool DirectivesPass(ExecutionContext ctx, GraphQLDirectives? directives) {
        if (directives == null) return true;
        foreach (var d in directives.Items) {
            var name = d.Name.StringValue;
            if (name != "skip" && name != "include") continue;
            var ifValue = false;
            var arg = d.Arguments?.Items.FirstOrDefault(a => a.Name.StringValue == "if");
            if (arg != null) {
                var v = ValueResolver.Resolve(ctx, arg.Value, new GqlNonNullType(ctx.Schema.Scalars.Boolean));
                ifValue = v is true;
            }
            if (name == "skip" && ifValue) return false;
            if (name == "include" && !ifValue) return false;
        }
        return true;
    }

    // ---- validation ----

    /// <summary>
    /// Validates the operation (and every fragment against its own type condition):
    /// fields must exist on their parent type, composites need a subselection, leaves must not have one,
    /// arguments must be declared, fragment type conditions must name known composite types.
    /// Throws <see cref="GraphQLRequestException"/> on the first violation.
    /// </summary>
    public static void Validate(ExecutionContext ctx, GraphQLOperationDefinition op) {
        if (op.SelectionSet == null) throw ctx.RequestError("The operation has no selection set.");
        validateSet(ctx.Schema.QueryType, op.SelectionSet, isRoot: true);
        foreach (var frag in ctx.Fragments.Values) {
            var conditionName = frag.TypeCondition.Type.Name.StringValue;
            var target = resolveCondition(conditionName, frag);
            if (frag.SelectionSet != null) validateSet(target, frag.SelectionSet, isRoot: false);
        }

        IGqlCompositeType resolveCondition(string name, ASTNode node) {
            if (isIntrospectionTypeName(name)) return null!; // introspection subtrees are validated leniently
            if (!ctx.Schema.TryGetType(name, out var t) || t is not IGqlCompositeType composite) {
                throw ctx.RequestError($"Unknown type \"{name}\" in fragment type condition.", node);
            }
            return composite;
        }

        static bool isIntrospectionTypeName(string name) => name.StartsWith("__", StringComparison.Ordinal);

        void validateSet(IGqlCompositeType? parent, GraphQLSelectionSet set, bool isRoot) {
            foreach (var sel in set.Selections) {
                switch (sel) {
                    case GraphQLField f: {
                            var name = f.Name.StringValue;
                            if (name == "__typename") {
                                if (f.SelectionSet != null) throw ctx.RequestError("Field \"__typename\" must not have a selection set.", f);
                                continue;
                            }
                            if (isRoot && (name == "__schema" || name == "__type")) {
                                if (!ctx.Options.EnableIntrospection) throw ctx.RequestError("Introspection is disabled on this endpoint.", f);
                                if (f.SelectionSet == null) throw ctx.RequestError($"Field \"{name}\" of type \"__{(name == "__schema" ? "Schema" : "Type")}\" must have a selection set.", f);
                                continue; // the introspection tree is projected leniently
                            }
                            if (parent == null) continue; // inside an introspection subtree
                            if (!parent.TryGetField(name, out var fieldDef)) {
                                throw ctx.RequestError($"Field \"{name}\" is not defined on type \"{parent.Name}\".", f);
                            }
                            if (f.Arguments != null) {
                                foreach (var a in f.Arguments.Items) {
                                    if (fieldDef.GetArgument(a.Name.StringValue) == null) {
                                        throw ctx.RequestError($"Unknown argument \"{a.Name.StringValue}\" on field \"{parent.Name}.{name}\".", a);
                                    }
                                }
                            }
                            var named = fieldDef.Type.UnwrapNamed();
                            if (named is IGqlCompositeType childComposite) {
                                if (f.SelectionSet == null) throw ctx.RequestError($"Field \"{name}\" of type \"{named.Name}\" must have a selection set.", f);
                                validateSet(childComposite, f.SelectionSet, isRoot: false);
                            } else {
                                if (f.SelectionSet != null) throw ctx.RequestError($"Field \"{name}\" of type \"{named.Name}\" must not have a selection set.", f);
                            }
                            break;
                        }
                    case GraphQLInlineFragment inf: {
                            var target = parent;
                            if (inf.TypeCondition != null) target = resolveCondition(inf.TypeCondition.Type.Name.StringValue, inf);
                            if (inf.SelectionSet != null) validateSet(target, inf.SelectionSet, isRoot: false);
                            break;
                        }
                    case GraphQLFragmentSpread sp: {
                            if (!ctx.Fragments.ContainsKey(sp.FragmentName.Name.StringValue)) {
                                throw ctx.RequestError($"Unknown fragment \"{sp.FragmentName.Name.StringValue}\".", sp);
                            }
                            break; // the fragment body is validated once against its own type condition
                        }
                }
            }
        }
    }
}
