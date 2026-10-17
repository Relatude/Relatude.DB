using System.Globalization;
using System.Text.RegularExpressions;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.Query.Data;

namespace Relatude.DB.NodeServer.UI;

sealed class UIPatternTester {
    readonly RelatudeDBServer _server;
    static readonly QueryContext adminContext = QueryContext.Default.Admin().Hidden().Unpublished().CultureFallbacks();
    static readonly TimeSpan matchTimeout = TimeSpan.FromSeconds(1);
    const int maxTestedValues = 200;
    const int maxStoredNodesChecked = 5000;
    const int maxStoredExamples = 12;
    internal UIPatternTester(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("datamodel-pattern-test", ctx => test(ctx.Payload<PatternTestPayload>()));
    }

    object test(PatternTestPayload p) {
        var rules = new StringPropertyModel {
            Trim = p.Trim,
            CaseTransform = Enum.TryParse<CaseTransform>(p.CaseTransform, true, out var transform) ? transform : CaseTransform.NoTransform,
        };
        Regex? regex = null;
        string? error = null;
        if (!string.IsNullOrEmpty(p.Pattern)) {
            try {
                regex = new Regex(p.Pattern, RegexOptions.None, matchTimeout);
            } catch (ArgumentException e) {
                error = RegularExpressionErrors.Describe(e);
            }
        }
        var results = (p.Values ?? []).Take(maxTestedValues).Select(value => {
            var stored = rules.ApplyTransforms(value);
            var (matches, reason) = check(regex, p.LegalCharacters, stored);
            return new { Value = value, Stored = stored, Changed = !string.Equals(stored, value, StringComparison.Ordinal), Matches = matches, Reason = reason };
        }).ToArray();
        return new { Error = error, Results = results, Stored = error == null ? checkStoredValues(p, regex) : null };
    }

    static (bool matches, string? reason) check(Regex? regex, string? legalCharacters, string value) {
        if (!string.IsNullOrEmpty(legalCharacters)) {
            var illegal = value.Where(c => !legalCharacters.Contains(c)).Distinct().ToArray();
            if (illegal.Length > 0) return (false, "Has characters that are not allowed: " + string.Join(" ", illegal.Select(c => c == ' ' ? "space" : "\"" + c + "\"")));
        }
        if (regex == null) return (true, null);
        try {
            if (regex.IsMatch(value)) return (true, null);
            return (false, value.Length == 0 ? "An empty value does not match, so a value is required" : "Does not match the pattern");
        } catch (RegexMatchTimeoutException) {
            return (false, "The pattern took too long on this value; simplify it, for example by removing nested repetitions like (a+)+");
        }
    }

    object? checkStoredValues(PatternTestPayload p, Regex? regex) {
        if (p.PropertyId is not Guid propertyId || p.TypeId is not Guid typeId) return null;
        if (regex == null && string.IsNullOrEmpty(p.LegalCharacters)) return null;
        if (!_server.Containers.TryGetValue(p.StoreId, out var c) || c.Store == null || c.Store.State != DataStoreState.Open) return null;
        var s = c.Store;
        if (!s.Datastore.Datamodel.NodeTypes.ContainsKey(typeId)) return null;
        var query = s.QueryType(typeId, adminContext) + ".Page(0, " + maxStoredNodesChecked.ToString(CultureInfo.InvariantCulture) + ")";
        if (s.Datastore.Query(query, [], adminContext) is not IStoreNodeDataCollection nodes) return null;
        var checkedNodes = 0;
        var failing = 0;
        var examples = new List<object>();
        foreach (var n in nodes.NodeValues) {
            checkedNodes++;
            var value = n.TryGetValue(propertyId, out var v) ? v as string ?? "" : "";
            var (matches, reason) = check(regex, p.LegalCharacters, value);
            if (matches) continue;
            failing++;
            if (examples.Count < maxStoredExamples) examples.Add(new { Value = value, Reason = reason });
        }
        return new { Checked = checkedNodes, Total = s.QueryType(typeId, adminContext).Count(), Failing = failing, Examples = examples };
    }

    sealed record PatternTestPayload(Guid StoreId, string? Pattern, string[]? Values, bool Trim, string? CaseTransform, string? LegalCharacters, Guid? PropertyId, Guid? TypeId);
}
