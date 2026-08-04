using System.Globalization;
using Relatude.DB.Common;

namespace Relatude.DB.Query.Expressions;

/// <summary>
/// x.Body.MatchesSearch("wool jacket"): true when the string property matches the search according to
/// its own word index, and to its semantic index when it has one. That is the search of WhereSearch
/// narrowed to one property rather than the node's combined text index, and being a predicate it
/// composes with OR and NOT.
/// Unlike every other filter in a query expression this one has no row evaluation: reproducing a
/// search per row would take the tokenizer, the term expansion and the ranking of the word index. So
/// it is only ever answered from an index, and <see cref="Evaluate"/> exists to explain why a
/// property that has no word or semantic index cannot be searched, rather than to answer anything.
/// </summary>
public class MatchesSearchExpression : IExpression {
    public readonly VariableReferenceExpression SourceObject;
    public readonly string PropertyName;
    public readonly string SearchText;
    public readonly double? SemanticRatio;
    public readonly float? MinimumVectorSimilarity;
    public readonly bool? OrSearch;
    public readonly int? MaxWordsEvaluated;
    readonly PropertyReferenceExpression _propertyRef;
    public MatchesSearchExpression(string propertyPath, object? search, double? semanticRatio,
        float? minimumVectorSimilarity, bool? orSearch, int? maxWordsEvaluated) {
        (SourceObject, PropertyName) = StringMethodExpressionUtil.SplitPath(propertyPath, "MatchesSearch");
        _propertyRef = new PropertyReferenceExpression(SourceObject, PropertyName);
        SearchText = StringMethodExpressionUtil.ToSearchString(search, "MatchesSearch");
        SemanticRatio = semanticRatio;
        MinimumVectorSimilarity = minimumVectorSimilarity;
        OrSearch = orSearch;
        MaxWordsEvaluated = maxWordsEvaluated;
    }
    public object Evaluate(IVariables vars) {
        // reached only when the planner could not use an index, which is exactly the case that has to
        // fail loudly: answering with a substring test instead would quietly disagree with the
        // indexed path, which stems, expands and ranks terms
        var v = _propertyRef.Evaluate(vars);
        if (v is not null and not string)
            throw new NotSupportedException("MatchesSearch is only supported on string properties. " + SourceObject + "." + PropertyName + " is of type " + v.GetType().Name + ". ");
        throw new NotSupportedException("MatchesSearch requires " + SourceObject + "." + PropertyName
            + " to have a word or semantic index. Add IndexedByWords = true to its [StringProperty], or use Contains for a substring match, or WhereSearch to search the combined text index of the node. ");
    }
    public override string ToString() {
        var settings = SemanticRatio == null && MinimumVectorSimilarity == null && OrSearch == null && MaxWordsEvaluated == null
            ? string.Empty
            : ", " + string.Join(", ", arg(SemanticRatio), arg(MinimumVectorSimilarity), arg(OrSearch), arg(MaxWordsEvaluated));
        return SourceObject + "." + PropertyName + ".MatchesSearch(" + SearchText.ToStringLiteral() + settings + ")";
        static string arg(object? v) => v switch {
            null => "null",
            bool b => b.ToStringLiteral(),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "null",
        };
    }
}
