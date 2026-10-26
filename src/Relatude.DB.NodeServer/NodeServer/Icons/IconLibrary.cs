using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Xml;
using Relatude.DB.Datamodels;

namespace Relatude.DB.NodeServer.Icons;

/// <summary>
/// The icons the server carries for node types to be shown with (<see cref="NodeTypeModel.Icon"/>): open
/// icon sets in Iconify's JSON format, embedded in this assembly from NodeServer/Icons, which
/// src/Relatude.DB.UI/tools/generate-icon-library.py makes (LICENSES.md there holds each set's licence).
///
/// A type's icon is a name of the library, "set:name" ("lucide:house"), or the source of an SVG image.
/// <see cref="ToSvg"/> gives the SVG either way, for whoever shows the type - the CMS's API sends it to its
/// interface. The admin UI's icon picker downloads whole sets (<see cref="SetFile"/>) and draws them itself.
/// A set is read the first time one of its icons is asked for, and kept.
/// </summary>
public static class IconLibrary {
    /// <summary>One set of the library, as index.json describes it.</summary>
    public sealed class IconSet {
        public string Prefix { get; init; } = "";
        public string Name { get; init; } = "";
        /// <summary>The icons carry colours of their own; otherwise they are drawn in the colour of the text around them (currentColor).</summary>
        public bool Colour { get; init; }
        public int Total { get; init; }
        public string? Author { get; init; }
        public string? AuthorUrl { get; init; }
        public string? License { get; init; }
        public string? LicenseUrl { get; init; }
        public string[] Samples { get; init; } = [];
        /// <summary>A hash of the set's file, so the browser can keep it for good under a url that changes with it.</summary>
        public string Version { get; init; } = "";
        public string? Package { get; init; }
    }

    /// <summary>The longest SVG source taken as an icon.</summary>
    public const int MaxSvgLength = 100_000;

    const string resourcePrefix = "Relatude.DB.NodeServer.Icons.";
    static readonly Lazy<IReadOnlyList<IconSet>> index = new(readIndex);
    static readonly ConcurrentDictionary<string, Lazy<ParsedSet?>> parsed = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, string?> svgs = new(StringComparer.Ordinal);

    /// <summary>The sets of the library, monochrome ones first.</summary>
    public static IReadOnlyList<IconSet> Sets => index.Value;

    /// <summary>The set's file, gzipped JSON as embedded, for the admin UI's picker; null for a set the library does not have.</summary>
    public static byte[]? SetFile(string prefix) {
        if (!Sets.Any(s => s.Prefix == prefix)) return null;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourcePrefix + prefix + ".json.gz");
        if (stream == null) return null;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>True for a value that is the source of an SVG image rather than a name of the library.</summary>
    public static bool IsSvg(string icon) {
        var t = icon.TrimStart();
        return t.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || t.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The SVG of a type's icon: a name of the library made into its SVG, SVG source as it is written. Null
    /// for no icon, a name the library does not have, and SVG source that <see cref="IsValid"/> refuses, so
    /// what comes back can be shown as it is.
    /// </summary>
    public static string? ToSvg(string? icon) {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        icon = icon.Trim();
        return svgs.GetOrAdd(icon, i => IsSvg(i) ? (IsValid(i, out _) ? i : null) : named(i));
    }

    /// <summary>
    /// Whether the value can be a type's icon: a name the library has, or the source of an SVG image that
    /// parses, has svg as its root and holds no script (no script or foreignObject element, no on... event
    /// attribute, no javascript: link), so it is safe wherever it is put. Says what is wrong otherwise.
    /// </summary>
    public static bool IsValid(string icon, out string? problem) {
        problem = null;
        if (string.IsNullOrWhiteSpace(icon)) {
            problem = "The icon is empty.";
            return false;
        }
        icon = icon.Trim();
        if (!IsSvg(icon)) {
            if (named(icon) != null) return true;
            var colon = icon.IndexOf(':');
            problem = colon < 1 ? "\"" + icon + "\" is neither a name of the icon library, written set:name like lucide:house, nor the source of an SVG image."
                : !Sets.Any(s => s.Prefix == icon[..colon]) ? "The icon library has no set called \"" + icon[..colon] + "\". It has " + string.Join(", ", Sets.Select(s => s.Prefix)) + "."
                : "The icon set " + icon[..colon] + " has no icon called \"" + icon[(colon + 1)..] + "\".";
            return false;
        }
        if (icon.Length > MaxSvgLength) {
            problem = "The SVG is " + icon.Length.ToString("N0", CultureInfo.InvariantCulture) + " characters long; an icon may be at most " + MaxSvgLength.ToString("N0", CultureInfo.InvariantCulture) + ".";
            return false;
        }
        try {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(icon), settings);
            var root = true;
            while (reader.Read()) {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (root && reader.LocalName != "svg") {
                    problem = "The SVG's root element is <" + reader.LocalName + ">, not <svg>.";
                    return false;
                }
                root = false;
                if (reader.LocalName is "script" or "foreignObject") {
                    problem = "The SVG holds a <" + reader.LocalName + "> element. An icon may not hold scripts or other documents.";
                    return false;
                }
                while (reader.MoveToNextAttribute()) {
                    if (reader.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)) {
                        problem = "The SVG has an event attribute, " + reader.LocalName + ". An icon may not hold scripts.";
                        return false;
                    }
                    if (reader.Value.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) {
                        problem = "The SVG links to javascript:. An icon may not hold scripts.";
                        return false;
                    }
                }
            }
            if (root) {
                problem = "The SVG has no elements.";
                return false;
            }
        } catch (XmlException error) {
            problem = "The SVG does not parse: " + error.Message;
            return false;
        }
        return true;
    }

    public static IReadOnlyList<string> Names(string prefix) {
        var s = set(prefix);
        return s == null ? [] : s.Visible.Select(v => v.Name).ToArray();
    }

    public static string? Find(string prefix, string? wanted) {
        var s = set(prefix);
        if (s == null || string.IsNullOrWhiteSpace(wanted)) return null;
        var name = asIconName(wanted);
        if (name.Length == 0) return null;
        if (s.Icons.ContainsKey(name) || s.Aliases.ContainsKey(name)) return prefix + ":" + name;
        var words = name.Split('-');
        var found = s.Visible.Where(v => v.Name.StartsWith(name + "-", StringComparison.Ordinal)).Select(v => v.Name).MinBy(n => n.Length)
            ?? s.Visible.Where(v => words.All(v.Parts.Contains)).Select(v => v.Name).MinBy(n => n.Length);
        return found == null ? null : prefix + ":" + found;
    }

    static string asIconName(string wanted) {
        var text = wanted.Trim().ToLowerInvariant();
        var colon = text.LastIndexOf(':');
        if (colon >= 0) text = text[(colon + 1)..];
        var chars = text.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    // ---------------------------------------------------------------------- a set, read

    sealed record Icon(string Body, double? Left, double? Top, double? Width, double? Height);
    sealed record Alias(string Parent, double? Left, double? Top, double? Width, double? Height, int Rotate, bool HFlip, bool VFlip);
    sealed class ParsedSet {
        public double Left, Top, Width = 16, Height = 16;
        public readonly Dictionary<string, Icon> Icons = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Alias> Aliases = new(StringComparer.Ordinal);
        public readonly HashSet<string> Hidden = new(StringComparer.Ordinal);
        (string Name, string[] Parts)[]? visible;
        public (string Name, string[] Parts)[] Visible => visible ??= Icons.Keys.Where(n => !Hidden.Contains(n)).Select(n => (n, n.Split('-'))).ToArray();
    }

    static IReadOnlyList<IconSet> readIndex() {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourcePrefix + "index.json");
        if (stream == null) return [];
        using var doc = JsonDocument.Parse(stream);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return doc.RootElement.GetProperty("sets").Deserialize<IconSet[]>(options) ?? [];
    }

    static ParsedSet? set(string prefix) => parsed.GetOrAdd(prefix, p => new Lazy<ParsedSet?>(() => read(p))).Value;

    static ParsedSet? read(string prefix) {
        var file = SetFile(prefix);
        if (file == null) return null;
        using var gz = new GZipStream(new MemoryStream(file), CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gz);
        var root = doc.RootElement;
        var s = new ParsedSet {
            Left = number(root, "left") ?? 0,
            Top = number(root, "top") ?? 0,
            Width = number(root, "width") ?? 16,
            Height = number(root, "height") ?? 16,
        };
        foreach (var icon in root.GetProperty("icons").EnumerateObject()) {
            var v = icon.Value;
            s.Icons[icon.Name] = new Icon(v.GetProperty("body").GetString() ?? "", number(v, "left"), number(v, "top"), number(v, "width"), number(v, "height"));
            if (flag(v, "hidden")) s.Hidden.Add(icon.Name);
        }
        if (root.TryGetProperty("aliases", out var aliases)) {
            foreach (var alias in aliases.EnumerateObject()) {
                var v = alias.Value;
                s.Aliases[alias.Name] = new Alias(v.GetProperty("parent").GetString() ?? "", number(v, "left"), number(v, "top"), number(v, "width"), number(v, "height"),
                    (int)(number(v, "rotate") ?? 0), flag(v, "hFlip"), flag(v, "vFlip"));
            }
        }
        return s;
    }

    static double? number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    static bool flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // "set:name" as an SVG, the way Iconify draws it: the icon's box as the viewBox, an alias's turns and flips around its middle
    static string? named(string icon) {
        var colon = icon.IndexOf(':');
        if (colon < 1 || colon == icon.Length - 1) return null;
        var s = set(icon[..colon]);
        if (s == null) return null;
        var name = icon[(colon + 1)..];
        double? left = null, top = null, width = null, height = null;
        int rotate = 0;
        bool hFlip = false, vFlip = false;
        for (var depth = 0; depth < 8; depth++) {
            if (s.Icons.TryGetValue(name, out var found)) {
                var l = left ?? found.Left ?? s.Left;
                var t = top ?? found.Top ?? s.Top;
                var w = width ?? found.Width ?? s.Width;
                var h = height ?? found.Height ?? s.Height;
                var body = found.Body;
                var turns = new List<string>();
                if (rotate % 4 != 0) turns.Add("rotate(" + n(rotate % 4 * 90) + " " + n(l + w / 2) + " " + n(t + h / 2) + ")");
                if (hFlip) turns.Add("translate(" + n(2 * l + w) + " 0) scale(-1 1)");
                if (vFlip) turns.Add("translate(0 " + n(2 * t + h) + ") scale(1 -1)");
                if (turns.Count > 0) body = "<g transform=\"" + string.Join(" ", turns) + "\">" + body + "</g>";
                return "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"" + n(l) + " " + n(t) + " " + n(w) + " " + n(h) + "\" width=\"" + n(w) + "\" height=\"" + n(h) + "\">" + body + "</svg>";
            }
            if (!s.Aliases.TryGetValue(name, out var alias)) return null;
            // the alias nearest the name says the box; turns and flips add up along the way
            left ??= alias.Left;
            top ??= alias.Top;
            width ??= alias.Width;
            height ??= alias.Height;
            rotate += alias.Rotate;
            hFlip ^= alias.HFlip;
            vFlip ^= alias.VFlip;
            name = alias.Parent;
        }
        return null;
    }

    static string n(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
