using System.Text;
using System.Text.RegularExpressions;
using Relatude.DB.AI;

namespace Relatude.DB.NodeServer.Icons;

internal sealed record IconSuggestionType(Guid Id, string Name, string? Namespace, string? Kind, string[]? Bases, string[]? Properties);
internal sealed record IconSuggestion(Guid TypeId, string? Icon, string[] Alternatives);
internal sealed record IconSuggestions(IconSuggestion[] Types, int Calls, int Credits, int CreditsLeft, string? Model, string? Problem);

internal static partial class IconSuggester {
    public const int TypesPerCall = 120;
    public const int MaxTypes = 2000;
    const int candidatesPerType = 3;
    const int maxOutputTokens = 4096;
    const int parallelCalls = 4;
    const int maxListedNamesChars = 8000;
    const int maxPropertiesShown = 12;

    public static async Task<IconSuggestions> SuggestAsync(string prefix, IReadOnlyList<IconSuggestionType> types, Func<string, int, Task<RelatudeServicesCompletion>> complete, CancellationToken cancellation) {
        var set = IconLibrary.Sets.FirstOrDefault(s => s.Prefix == prefix) ?? throw new Exception("The icon library has no set called \"" + prefix + "\".");
        if (types.Count == 0) throw new Exception("There are no types to find icons for.");
        if (types.Count > MaxTypes) throw new Exception("At most " + MaxTypes + " types can be given icons at a time.");
        var names = IconLibrary.Names(prefix);
        var listed = names.Sum(n => n.Length + 1) <= maxListedNamesChars ? names : null;
        var answers = new Dictionary<Guid, string[]>();
        var tally = new Tally();

        await askAsync(set, listed, types, complete, answers, tally, cancellation);
        var unanswered = types.Where(t => !answers.ContainsKey(t.Id)).ToArray();
        if (unanswered.Length > 0 && tally.Calls > tally.Failures) await askAsync(set, listed, unanswered, complete, answers, tally, cancellation);
        if (answers.Count == 0 && tally.Problem != null) throw new Exception(tally.Problem);

        var suggestions = types.Select(t => {
            var found = answers.TryGetValue(t.Id, out var wanted) ? wanted.Select(w => IconLibrary.Find(prefix, w)).OfType<string>().Distinct().ToArray() : [];
            return new IconSuggestion(t.Id, found.FirstOrDefault(), found.Skip(1).ToArray());
        }).ToArray();
        return new IconSuggestions(suggestions, tally.Calls, tally.Credits, tally.CreditsLeft, tally.Model, tally.Problem);
    }

    sealed class Tally {
        public int Calls, Failures, Credits, CreditsLeft = -1;
        public string? Model, Problem;
    }

    static async Task askAsync(IconLibrary.IconSet set, IReadOnlyList<string>? listed, IReadOnlyList<IconSuggestionType> types, Func<string, int, Task<RelatudeServicesCompletion>> complete,
        Dictionary<Guid, string[]> answers, Tally tally, CancellationToken cancellation) {
        using var gate = new SemaphoreSlim(parallelCalls);
        var batches = types.Chunk(TypesPerCall).Select(async batch => {
            await gate.WaitAsync(cancellation);
            try {
                var completion = await complete(Prompt(set, listed, batch), maxOutputTokens).WaitAsync(cancellation);
                var picked = Parse(completion.Text);
                lock (tally) {
                    tally.Calls++;
                    tally.Credits += completion.Credits;
                    tally.CreditsLeft = completion.CreditsLeft;
                    tally.Model ??= completion.Model;
                    for (var i = 0; i < batch.Length; i++) {
                        if (picked.TryGetValue(i + 1, out var wanted) && wanted.Length > 0) answers[batch[i].Id] = wanted;
                    }
                }
            } catch (Exception error) when (error is not OperationCanceledException) {
                lock (tally) {
                    tally.Calls++;
                    tally.Failures++;
                    tally.Problem ??= error.Message;
                }
            } finally {
                gate.Release();
            }
        }).ToArray();
        await Task.WhenAll(batches);
    }

    public static string Prompt(IconLibrary.IconSet set, IReadOnlyList<string>? listed, IReadOnlyList<IconSuggestionType> types) {
        var p = new StringBuilder();
        p.Append("Pick icons for the node types of a database's data model. An admin interface shows each type with one icon from the icon set \"")
            .Append(set.Name).Append("\" (").Append(set.Colour ? "colour icons" : "monochrome icons").AppendLine(").");
        p.AppendLine();
        if (listed != null) {
            p.AppendLine("These are the names of all the icons in the set, separated by spaces. Use only names from this list, spelled exactly as written:");
            p.AppendLine(string.Join(' ', listed));
        } else {
            p.Append("Write each icon's name exactly as the set names it in Iconify");
            if (!string.IsNullOrEmpty(set.Package)) p.Append(" (").Append(set.Package).Append(')');
            p.Append(": lowercase English words joined by hyphens");
            if (set.Samples.Length > 0) p.Append(", such as ").Append(string.Join(", ", set.Samples));
            p.AppendLine(". Use only names you know the set has.");
        }
        p.AppendLine();
        p.Append("For every type below, give the ").Append(candidatesPerType).AppendLine(" icons that best show what the type stands for, the best first.");
        p.AppendLine("Judge by the type's name and namespace, the types it inherits from and its properties. A type that groups others, such as a base class or an interface, gets an icon for the whole group.");
        p.AppendLine("Prefer different icons for different types when the match is as good.");
        p.AppendLine();
        var example = set.Samples.Concat(listed ?? []).Distinct().Take(candidatesPerType * 2).Select(n => "\"" + n + "\"").ToArray();
        if (example.Length < candidatesPerType * 2) example = ["\"house\"", "\"building\"", "\"map-pin\"", "\"user\"", "\"contact\"", "\"id-card\""];
        p.Append("Answer with one JSON object and nothing else: the type's number as the key and its icon names as an array, for example {\"1\":[")
            .Append(string.Join(',', example.Take(candidatesPerType))).Append("],\"2\":[").Append(string.Join(',', example.Skip(candidatesPerType))).AppendLine("]}.");
        p.AppendLine();
        p.AppendLine("The types, one per line: number. namespace.name [kind] : the types it inherits from (its properties)");
        for (var i = 0; i < types.Count; i++) p.Append(i + 1).Append(". ").AppendLine(describe(types[i]));
        return p.ToString();
    }

    static string describe(IconSuggestionType t) {
        var line = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(t.Namespace)) line.Append(clip(t.Namespace, 120)).Append('.');
        line.Append(clip(t.Name, 120));
        if (!string.IsNullOrWhiteSpace(t.Kind) && t.Kind != "Class") line.Append(" [").Append(clip(t.Kind, 20).ToLowerInvariant()).Append(']');
        var bases = (t.Bases ?? []).Where(b => !string.IsNullOrWhiteSpace(b)).Take(6).Select(b => clip(b, 80)).ToArray();
        if (bases.Length > 0) line.Append(" : ").Append(string.Join(", ", bases));
        var properties = (t.Properties ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Take(maxPropertiesShown).Select(n => clip(n, 60)).ToArray();
        if (properties.Length > 0) line.Append(" (").Append(string.Join(", ", properties)).Append(')');
        return line.ToString();
    }

    static string clip(string text, int length) {
        var flat = text.Trim().ReplaceLineEndings(" ");
        return flat.Length <= length ? flat : flat[..length];
    }

    public static Dictionary<int, string[]> Parse(string answer) {
        var picked = new Dictionary<int, string[]>();
        foreach (Match entry in entryPattern().Matches(answer)) {
            if (!int.TryParse(entry.Groups[1].Value, out var number)) continue;
            picked[number] = namePattern().Matches(entry.Groups[2].Value).Select(m => m.Groups[1].Value.Trim()).Where(n => n.Length > 0).Take(candidatesPerType * 2).ToArray();
        }
        return picked;
    }

    [GeneratedRegex("\"?(\\d+)\"?\\s*:\\s*\\[([^\\]]*)\\]")]
    private static partial Regex entryPattern();

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex namePattern();
}
