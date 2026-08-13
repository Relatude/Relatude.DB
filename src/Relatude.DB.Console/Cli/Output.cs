using System.Text;
using System.Text.Json;
using Relatude.DB.NodeServer.Json;

namespace Relatude.DB.Cli;

/// <summary>
/// All console output goes through here. The result of a command is written to stdout, everything else
/// (progress, database log, warnings) to stderr, so <c>--json</c> output stays machine readable even
/// though the database writes its own log to the console while opening.
///
/// <para>The obvious name for this file would have been Output.cs, but CON is a reserved device name on
/// Windows and git cannot add a file called that - it ends up silently missing from a commit.</para>
/// </summary>
public static class Output {
    static TextWriter _result = System.Console.Out;
    static bool _quiet;
    static bool _verbose;
    public static bool Verbose => _verbose;
    public static bool Quiet => _quiet;

    /// <summary>
    /// Keeps the real stdout for results and points <see cref="System.Console"/> at stderr, so library
    /// code that writes to the console cannot corrupt the result stream.
    /// </summary>
    public static void Initialize(bool quiet, bool verbose) {
        _quiet = quiet;
        _verbose = verbose;
        // setting the encoding replaces the standard writers, so it has to happen before one is kept
        try {
            System.Console.OutputEncoding = Encoding.UTF8;
        } catch (IOException) { } // no console attached
        _result = System.Console.Out;
        // library code writes progress straight to the console: keep it off the result stream, and out of
        // the way unless it was asked for
        System.Console.SetOut(verbose && !quiet ? System.Console.Error : TextWriter.Null);
    }

    public static void Write(string text) => _result.Write(text);
    public static void WriteLine(string text = "") => _result.WriteLine(text);
    public static void Flush() => _result.Flush();

    /// <summary>Progress and other diagnostics. Suppressed by --quiet.</summary>
    public static void Info(string text) {
        if (!_quiet) System.Console.Error.WriteLine(text);
    }
    /// <summary>Detail only wanted with --verbose.</summary>
    public static void Detail(string text) {
        if (_verbose && !_quiet) System.Console.Error.WriteLine(text);
    }
    public static void Warn(string text) {
        if (!_quiet) System.Console.Error.WriteLine("warning: " + text);
    }
    public static void Error(string text) => System.Console.Error.WriteLine("error: " + text);

    static JsonSerializerOptions? _json;
    /// <summary>
    /// The same serializer the HTTP API uses (camel case, relation aware), so a query result printed
    /// here has the shape a client would receive.
    /// </summary>
    public static JsonSerializerOptions JsonOptions {
        get {
            if (_json == null) {
                _json = new JsonSerializerOptions();
                RelatudeDBJsonOptions.ConfigureDefault(_json);
                _json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                _json.WriteIndented = true;
            }
            return _json;
        }
    }
    public static void Json(object? value) => WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    public static string Bytes(long bytes) {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return (u == 0 ? v.ToString("0") : v.ToString("0.##")) + " " + units[u];
    }
    /// <summary>Two column key/value list, aligned on the longest key.</summary>
    public static void Table(IEnumerable<(string Key, string Value)> rows, string indent = "  ") {
        var list = rows.ToList();
        if (list.Count == 0) return;
        var width = list.Max(r => r.Key.Length);
        foreach (var (key, value) in list) WriteLine(indent + key.PadRight(width) + "  " + value);
    }
}
