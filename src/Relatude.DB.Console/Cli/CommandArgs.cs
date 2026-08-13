namespace Relatude.DB.Cli;

/// <summary>Thrown when the command line itself is wrong. Reported without a stack trace and with exit code 2.</summary>
public class UsageException(string message) : Exception(message) { }

/// <summary>
/// The parsed command line: one command, any number of positional values and named options.
/// Options are written as <c>--name value</c>, <c>--name=value</c> or as a flag <c>--name</c>, and may repeat.
/// Names are matched case insensitively. Every command calls <see cref="Accept"/> so an unknown or
/// misspelled option is an error instead of being silently ignored.
/// </summary>
public sealed class CommandArgs {
    readonly Dictionary<string, List<string?>> _options = new(StringComparer.OrdinalIgnoreCase);
    CommandArgs() { }
    public string Command { get; private set; } = string.Empty;
    public List<string> Positional { get; } = [];

    public static CommandArgs Parse(string[] args) {
        var a = new CommandArgs();
        var i = 0;
        if (args.Length > 0 && !args[0].StartsWith('-')) {
            a.Command = args[0].ToLowerInvariant();
            i = 1;
        }
        for (; i < args.Length; i++) {
            var arg = args[i];
            if (arg == "--") { // everything after -- is positional, for queries starting with a dash
                for (i++; i < args.Length; i++) a.Positional.Add(args[i]);
                break;
            }
            if (arg.StartsWith("--") || (arg.StartsWith('-') && arg.Length == 2)) {
                var name = arg.TrimStart('-');
                string? value = null;
                var eq = name.IndexOf('=');
                if (eq >= 0) {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                } else if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) {
                    // a lone "-" or a negative number is a value, not the next option
                    var next = args[i + 1];
                    if (!next.StartsWith('-') || next.Length == 1 || char.IsDigit(next[1]) || next[1] == '.') {
                        value = next;
                        i++;
                    }
                }
                if (name.Length == 0) throw new UsageException("Empty option name in \"" + arg + "\".");
                if (!a._options.TryGetValue(name, out var values)) a._options[name] = values = [];
                values.Add(value);
            } else {
                a.Positional.Add(arg);
            }
        }
        return a;
    }

    public bool Has(string name) => _options.ContainsKey(name);
    public bool Flag(string name) {
        if (!_options.TryGetValue(name, out var values)) return false;
        var v = values[^1];
        if (v == null) return true;
        return v.ToLowerInvariant() switch {
            "true" or "yes" or "1" or "on" => true,
            "false" or "no" or "0" or "off" => false,
            _ => throw new UsageException($"--{name} takes true or false, not \"{v}\"."),
        };
    }
    public string? Get(string name) {
        if (!_options.TryGetValue(name, out var values)) return null;
        return values[^1] ?? throw new UsageException($"--{name} needs a value.");
    }
    public string Require(string name)
        => Get(name) ?? throw new UsageException($"--{name} is required.");
    public string[] GetAll(string name) {
        if (!_options.TryGetValue(name, out var values)) return [];
        return [.. values.Select(v => v ?? throw new UsageException($"--{name} needs a value."))];
    }
    public int? GetInt(string name) {
        var v = Get(name);
        if (v == null) return null;
        if (!int.TryParse(v, out var i)) throw new UsageException($"--{name} takes a whole number, not \"{v}\".");
        return i;
    }
    /// <summary>The single positional value, or null. Throws when more than one was given.</summary>
    public string? SinglePositional(string what) {
        if (Positional.Count == 0) return null;
        if (Positional.Count > 1) throw new UsageException($"Expected one {what}, got {Positional.Count}: " + string.Join(", ", Positional.Select(p => "\"" + p + "\"")));
        return Positional[0];
    }
    /// <summary>Fails on any option this command does not understand, suggesting the closest known name.</summary>
    public void Accept(params string[] known) {
        var all = known.Concat(Program.GlobalOptions).ToArray();
        foreach (var name in _options.Keys) {
            if (all.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var closest = all.OrderBy(k => distance(k.ToLowerInvariant(), name.ToLowerInvariant())).First();
            var hint = distance(closest.ToLowerInvariant(), name.ToLowerInvariant()) <= 3 ? $" Did you mean --{closest}?" : string.Empty;
            throw new UsageException($"Unknown option --{name} for \"{(Command.Length == 0 ? "relatude" : Command)}\".{hint}");
        }
    }
    static int distance(string a, string b) { // plain Levenshtein, only used to suggest a name
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++) {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++) {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
