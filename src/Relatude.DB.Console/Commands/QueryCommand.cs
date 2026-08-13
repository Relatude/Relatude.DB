using System.Globalization;
using System.Text.Json;
using Relatude.DB.Query;

namespace Relatude.DB.Cli.Commands;

/// <summary>
/// Runs a query in its text form and prints the result as JSON. The text form is what the HTTP API and
/// the admin UI send, and mirrors the typed query API: a node type followed by method calls.
/// </summary>
public static class QueryCommand {
    public static async Task<int> RunAsync(CommandArgs args) {
        args.Accept([.. Target.Options, .. ModelSource.Options, .. StoreHost.Options, "file", "param", "raw"]);
        var query = args.Get("file") is string file
            ? File.ReadAllText(Path.GetFullPath(file, Directory.GetCurrentDirectory())).Trim()
            : args.SinglePositional("query");
        if (string.IsNullOrWhiteSpace(query)) {
            throw new UsageException("No query given. Example: relatude query \"Product.Where(p => p.Price > 100).Take(10)\"");
        }
        var parameters = args.GetAll("param").Select(parseParameter).ToList();
        var target = Target.Resolve(args);
        using var host = await StoreHost.OpenAsync(args, target);
        Output.Detail("Query: " + query);
        var options = args.Flag("raw")
            ? new JsonSerializerOptions(Output.JsonOptions) { WriteIndented = false }
            : Output.JsonOptions;
        string json;
        try {
            // the result is enumerated lazily, so serializing is still part of running the query
            var result = await host.Store.EvaluateForJsonAsync(query, parameters);
            json = JsonSerializer.Serialize(result, options);
        } catch (Exception err) {
            throw new CliException(describe(err, host, query), err);
        }
        Output.WriteLine(json);
        return 0;
    }

    static Parameter parseParameter(string text) {
        var eq = text.IndexOf('=');
        if (eq <= 0) throw new UsageException("--param takes name=value, not \"" + text + "\".");
        var name = text[..eq];
        var raw = text[(eq + 1)..];
        return new Parameter(name, value(raw));
    }
    /// <summary>Values are typed by their shape, so numeric and boolean comparisons work as written.</summary>
    static object value(string raw) {
        if (raw.Length > 1 && raw[0] == '"' && raw[^1] == '"') return raw[1..^1];
        if (bool.TryParse(raw, out var b)) return b;
        if (int.TryParse(raw, CultureInfo.InvariantCulture, out var i)) return i;
        if (long.TryParse(raw, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(raw, CultureInfo.InvariantCulture, out var d)) return d;
        if (Guid.TryParse(raw, out var g)) return g;
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)) return dt;
        return raw;
    }

    /// <summary>A query that names something that is not in the model is the common mistake: say what is.</summary>
    static string describe(Exception err, StoreHost host, string query) {
        var message = err.Message;
        for (var e = err.InnerException; e != null; e = e.InnerException) message += Environment.NewLine + e.Message;
        var firstWord = new string([.. query.TakeWhile(char.IsLetterOrDigit)]);
        if (firstWord.Length > 0 && !host.Datamodel.NodeTypesByShortName.ContainsKey(firstWord)
            && !host.Datamodel.NodeTypesByFullName.ContainsKey(firstWord)) {
            var known = host.Datamodel.NodeTypes.Values
                .Where(t => !ModelSource.IsNative(t)).Select(t => t.CodeName).Order().Take(30);
            message += Environment.NewLine + "\"" + firstWord + "\" is not a node type in this datamodel. Known types: "
                + (known.Any() ? string.Join(", ", known) : "(none)");
        }
        return message;
    }
}
