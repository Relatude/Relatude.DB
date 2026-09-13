using System.Globalization;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Query.Data;
using Relatude.DB.Query.Expressions;

namespace Relatude.DB.Query.Methods;

/// <summary>
/// Which words the nodes of a result hold in a word-indexed string property, and how often, read out
/// of the word index rather than out of the nodes (<see cref="IWordSource"/>):
/// Words(property[, maxWords[, minDocuments[, minWordLength[, maxPostingsEvaluated]]]]), with
/// IgnoreWords(...) chained on for the words to skip. Evaluated against the collection it was
/// chained onto, or the selection of a facet clause. Not a page of anything: the count is over the
/// whole result, however large, because no node is read to produce it.
/// </summary>
public class WordsMethod : IExpression {
    readonly IExpression _input;
    readonly string _property;
    readonly Guid _propertyId;
    readonly int? _maxWords;
    readonly int? _minDocuments;
    readonly int? _minWordLength;
    readonly long? _maxPostingsEvaluated;
    readonly List<string> _ignore = new();

    public WordsMethod(IExpression input, Datamodel dm, string property, int? maxWords = null, int? minDocuments = null, int? minWordLength = null, long? maxPostingsEvaluated = null) {
        _input = input;
        _property = property;
        _propertyId = BucketGroups.PropertyId(dm, property);
        if (maxWords is <= 0) throw new ArgumentOutOfRangeException(nameof(maxWords), "Max words must be greater than 0. ");
        if (minDocuments is < 1) throw new ArgumentOutOfRangeException(nameof(minDocuments), "Min documents must be 1 or more. ");
        if (minWordLength is < 0) throw new ArgumentOutOfRangeException(nameof(minWordLength), "Min word length must be 0 or more. ");
        if (maxPostingsEvaluated is < 0) throw new ArgumentOutOfRangeException(nameof(maxPostingsEvaluated), "Max postings evaluated must be 0 (no limit) or more. ");
        _maxWords = maxWords;
        _minDocuments = minDocuments;
        _minWordLength = minWordLength;
        _maxPostingsEvaluated = maxPostingsEvaluated;
    }
    /// <summary>Words to leave out whatever their count. Compared as the index holds them: lowercase, no punctuation.</summary>
    public void IgnoreWords(IEnumerable<string> words) {
        foreach (var word in words) {
            var w = word.Trim();
            if (w.Length > 0) _ignore.Add(w);
        }
    }

    public object Evaluate(IVariables vars) {
        var nodes = TerminalSource.Nodes(_input, vars, "Words");
        if (nodes is not IWordSource source) throw new Exception("Words() cannot count the words of this collection without reading its nodes. ");
        var options = new WordCountOptions();
        if (_maxWords is int maxWords) options = options with { MaxWords = maxWords };
        if (_minDocuments is int minDocuments) options = options with { MinDocuments = minDocuments };
        if (_minWordLength is int minWordLength) options = options with { MinWordLength = minWordLength };
        if (_maxPostingsEvaluated is long maxPostings) options = options with { MaxPostingsEvaluated = maxPostings };
        if (_ignore.Count > 0) options = options with { Ignore = _ignore.ToHashSet() };
        var counted = source.Words(_propertyId, options, vars.Context);
        return new WordsQueryResultData(_propertyId, counted, nodes.TotalCount);
    }
    public override string ToString() {
        var sb = new System.Text.StringBuilder();
        sb.Append(_input).Append(".Words(").Append(_property.ToStringLiteral());
        // the options given, up to the last of them, in the positions they are read from; a gap
        // before the last one is filled with the default it stands for
        var defaults = new WordCountOptions();
        (string? Given, string Default)[] options = [
            (_maxWords?.ToString(CultureInfo.InvariantCulture), defaults.MaxWords.ToString(CultureInfo.InvariantCulture)),
            (_minDocuments?.ToString(CultureInfo.InvariantCulture), defaults.MinDocuments.ToString(CultureInfo.InvariantCulture)),
            (_minWordLength?.ToString(CultureInfo.InvariantCulture), defaults.MinWordLength.ToString(CultureInfo.InvariantCulture)),
            (_maxPostingsEvaluated?.ToString(CultureInfo.InvariantCulture), defaults.MaxPostingsEvaluated.ToString(CultureInfo.InvariantCulture)),
        ];
        var last = Array.FindLastIndex(options, o => o.Given != null);
        for (var i = 0; i <= last; i++) sb.Append(", ").Append(options[i].Given ?? options[i].Default);
        sb.Append(')');
        if (_ignore.Count > 0) sb.Append(".IgnoreWords(").Append(string.Join(", ", _ignore.Select(w => w.ToStringLiteral()))).Append(')');
        return sb.ToString();
    }
}
