using Relatude.DB.Common;
using Relatude.DB.FileConversion;
using System.Globalization;
using System.Text;

namespace Relatude.DB.Web;

/// <summary>
/// Readable, lossless URL representations of <see cref="FileAdjustment"/> - the alternative to
/// carrying the adjustment inside the opaque asset token (see <see cref="AssetUrlFormat"/>).
/// Two framings share one key set: query parameters ("w=100&amp;h=200&amp;f=jpeg") and a compact
/// path segment ("w100h200fjpeg"). Keys not set on the adjustment are omitted, unknown keys fail
/// the parse, and every property of the image, video and meta adjustments has a key, so encode
/// followed by parse reproduces an equivalent adjustment.
/// </summary>
public static class FileAdjustmentUrlCodec {

    // one key table for both framings. Value kinds decide the characters a short-string value may
    // use, which is what makes the separator-free framing parseable: numeric and flag values stop
    // at the first letter, name values (enum names, hex colors) are resolved by backtracking.
    enum kind { Int, Double, Flag, Name }
    sealed record keyDef(string Key, kind Kind);
    static readonly keyDef[] _keys = [
        new("k", kind.Name),      // adjustment type: i (image, default), v (video), m (meta)
        new("f", kind.Name),      // RequestedFormat, enum name
        new("w", kind.Int),       // Width
        new("h", kind.Int),       // Height
        new("q", kind.Int),       // Quality
        new("crop", kind.Name),   // CropMode, enum name
        new("zm", kind.Double),   // Zoom
        new("fx", kind.Int),      // FocusX
        new("fy", kind.Int),      // FocusY
        new("ox", kind.Int),      // OffsetX
        new("oy", kind.Int),      // OffsetY
        new("rot", kind.Double),  // Rotation
        new("bri", kind.Double),  // Brightness
        new("con", kind.Double),  // Contrast
        new("sat", kind.Double),  // Saturation
        new("hue", kind.Double),  // HueShift
        new("sha", kind.Double),  // Sharpness
        new("inv", kind.Flag),    // InvertLuminance
        new("ald", kind.Name),    // AutoLightDarkMode, enum name
        new("bg", kind.Name),     // BackgroundColor, hex without '#'
        new("abg", kind.Flag),    // AutoBackgroundColor
        new("tms", kind.Double),  // TimeOffsetMs
        new("tpc", kind.Double),  // TimeOffsetPercentage
        new("br", kind.Double),   // TargetBitRateInMbps (video)
        new("cnz", kind.Flag),    // CropNotZoom (video)
        new("tmp", kind.Flag),    // Temporary
    ];
    static readonly keyDef[] _keysLongestFirst = [.. _keys.OrderByDescending(k => k.Key.Length)];

    // encoding ///////////////////////////////////////////////////////////////////////////////////

    /// <summary>The adjustment as query parameters without a leading "?", e.g. "w=100&amp;h=200&amp;f=jpeg". False for adjustment types the codec does not know.</summary>
    public static bool TryToQueryString(FileAdjustment adjustment, out string query) {
        return tryEncode(adjustment, pair: (sb, key, value) => {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(key).Append('=').Append(value);
        }, out query);
    }
    /// <summary>The adjustment as a compact path segment, e.g. "w100h200fjpeg". False for adjustment types the codec does not know.</summary>
    public static bool TryToShortString(FileAdjustment adjustment, out string shortString) {
        return tryEncode(adjustment, pair: (sb, key, value) => sb.Append(key).Append(value), out shortString);
    }
    static bool tryEncode(FileAdjustment adjustment, Action<StringBuilder, string, string> pair, out string result) {
        result = string.Empty;
        var sb = new StringBuilder(48);
        switch (adjustment) {
            case FileAdjustmentImage img:
                pair(sb, "f", name(img.RequestedFormat));
                num(sb, "w", img.Width); num(sb, "h", img.Height); num(sb, "q", img.Quality);
                if (img.CropMode != null) pair(sb, "crop", name(img.CropMode.Value));
                dbl(sb, "zm", img.Zoom);
                num(sb, "fx", img.FocusX); num(sb, "fy", img.FocusY);
                num(sb, "ox", img.OffsetX); num(sb, "oy", img.OffsetY);
                dbl(sb, "rot", img.Rotation);
                dbl(sb, "bri", img.Brightness); dbl(sb, "con", img.Contrast);
                dbl(sb, "sat", img.Saturation); dbl(sb, "hue", img.HueShift); dbl(sb, "sha", img.Sharpness);
                flag(sb, "inv", img.InvertLuminance);
                if (img.AutoLightDarkMode != null) pair(sb, "ald", name(img.AutoLightDarkMode.Value));
                if (!string.IsNullOrEmpty(img.BackgroundColor)) pair(sb, "bg", img.BackgroundColor.TrimStart('#').ToLowerInvariant());
                flag(sb, "abg", img.AutoBackgroundColor);
                dbl(sb, "tms", img.TimeOffsetMs); dbl(sb, "tpc", img.TimeOffsetPercentage);
                if (img.Temporary) pair(sb, "tmp", "1");
                break;
            case FileAdjustmentVideo vid:
                pair(sb, "k", "v");
                pair(sb, "f", name(vid.RequestedFormat));
                num(sb, "w", vid.Width); num(sb, "h", vid.Height);
                if (vid.TargetBitRateInMbps != 0) pair(sb, "br", dblString(vid.TargetBitRateInMbps));
                if (vid.CropNotZoom) pair(sb, "cnz", "1");
                if (vid.Temporary) pair(sb, "tmp", "1");
                break;
            case FileAdjustmentMeta meta:
                pair(sb, "k", "m");
                pair(sb, "f", name(meta.RequestedFormat));
                if (!meta.Temporary) pair(sb, "tmp", "0"); // meta defaults to temporary
                break;
            default:
                return false; // an adjustment type the codec does not know: keep it inside the token
        }
        result = sb.ToString();
        return true;

        void num(StringBuilder b, string key, int? value) { if (value != null) pair(b, key, value.Value.ToString(CultureInfo.InvariantCulture)); }
        void dbl(StringBuilder b, string key, double? value) { if (value != null) pair(b, key, dblString(value.Value)); }
        void flag(StringBuilder b, string key, bool? value) { if (value != null) pair(b, key, value.Value ? "1" : "0"); }
    }
    static string name<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
    static string dblString(double value) => value.ToString("0.############", CultureInfo.InvariantCulture); // never scientific notation, so values stay in the short-string charset

    // decoding ///////////////////////////////////////////////////////////////////////////////////

    /// <summary>The adjustment carried in the query string of the URL, or null when no adjustment keys are present or a value is invalid. Unknown query parameters are ignored.</summary>
    public static FileAdjustment? TryParseQuery(string completeUrl) {
        Dictionary<string, string>? found = null;
        foreach (var def in _keys) {
            var value = UrlUtil.GetQueryParameter(completeUrl, def.Key);
            if (value == null) continue;
            found ??= [];
            found[def.Key] = value;
        }
        return found == null ? null : build(found);
    }
    /// <summary>The adjustment encoded in a short string produced by <see cref="TryToShortString"/>, or null when the string is not one.</summary>
    public static FileAdjustment? TryParseShortString(string shortString) {
        if (string.IsNullOrEmpty(shortString)) return null;
        var found = new Dictionary<string, string>();
        return tryParseShort(shortString, 0, found) ? build(found) : null;
    }
    static bool tryParseShort(string s, int pos, Dictionary<string, string> found) {
        if (pos >= s.Length) return found.Count > 0;
        foreach (var def in _keysLongestFirst) {
            if (found.ContainsKey(def.Key)) continue; // a key appears at most once
            if (string.CompareOrdinal(s, pos, def.Key, 0, def.Key.Length) != 0) continue;
            var valueStart = pos + def.Key.Length;
            var max = valueStart;
            while (max < s.Length && isValueChar(s[max], def.Kind)) max++;
            if (max == valueStart) continue; // no value, try a shorter key
            if (def.Kind == kind.Name) {
                // a name value may run into the next key ("fjpegw100"): backtrack from the longest
                // run, accepting only valid names so the right split is found
                for (var end = max; end > valueStart; end--) {
                    var value = s[valueStart..end];
                    if (!isValidNameValue(def.Key, value)) continue;
                    found[def.Key] = value;
                    if (tryParseShort(s, end, found)) return true;
                    found.Remove(def.Key);
                }
            } else {
                // numeric and flag values stop at the first letter, so the run is unambiguous
                found[def.Key] = s[valueStart..max];
                if (tryParseShort(s, max, found)) return true;
                found.Remove(def.Key);
            }
        }
        return false;
    }
    static bool isValidNameValue(string key, string value) => key switch {
        "f" => tryEnumName<FileFormat>(value, out _),
        "crop" => tryEnumName<ImageCropMode>(value, out _),
        "ald" => tryEnumName<AutoLightDarkSwitch>(value, out _),
        "k" => value is "i" or "v" or "m",
        "bg" => value.All(char.IsAsciiHexDigit),
        _ => true,
    };
    static bool tryEnumName<T>(string value, out T result) where T : struct, Enum {
        result = default;
        // names only: Enum.TryParse would also accept numeric strings like "100"
        if (value.Length == 0 || !char.IsAsciiLetter(value[0])) return false;
        return Enum.TryParse(value, true, out result);
    }
    static bool isValueChar(char c, kind k) => k switch {
        kind.Int => char.IsAsciiDigit(c) || c == '-',
        kind.Double => char.IsAsciiDigit(c) || c == '-' || c == '.',
        kind.Flag => c == '0' || c == '1',
        kind.Name => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c),
        _ => false,
    };

    static FileAdjustment? build(Dictionary<string, string> found) {
        found.TryGetValue("k", out var adjustmentKind);
        try {
            switch (adjustmentKind) {
                case null or "i": {
                        var img = new FileAdjustmentImage();
                        if (!apply(found, img)) return null;
                        return img;
                    }
                case "v": {
                        var vid = new FileAdjustmentVideo();
                        if (found.TryGetValue("f", out var f) && !fmt(f, vid)) return null;
                        if (found.TryGetValue("w", out var w)) vid.Width = int.Parse(w, CultureInfo.InvariantCulture);
                        if (found.TryGetValue("h", out var h)) vid.Height = int.Parse(h, CultureInfo.InvariantCulture);
                        if (found.TryGetValue("br", out var br)) vid.TargetBitRateInMbps = double.Parse(br, CultureInfo.InvariantCulture);
                        if (found.TryGetValue("cnz", out var cnz)) vid.CropNotZoom = cnz == "1";
                        if (found.TryGetValue("tmp", out var tmp)) vid.Temporary = tmp == "1";
                        return vid;
                    }
                case "m": {
                        var meta = new FileAdjustmentMeta();
                        if (found.TryGetValue("f", out var f) && !fmt(f, meta)) return null;
                        if (found.TryGetValue("tmp", out var tmp)) meta.Temporary = tmp == "1";
                        return meta;
                    }
                default: return null;
            }
        } catch {
            return null; // an unparsable value is a non-match, not an error
        }
    }
    static bool apply(Dictionary<string, string> found, FileAdjustmentImage img) {
        foreach (var (key, value) in found) {
            switch (key) {
                case "f": if (!fmt(value, img)) return false; break;
                case "w": img.Width = i(value); break;
                case "h": img.Height = i(value); break;
                case "q": img.Quality = i(value); break;
                case "crop": if (!tryEnumName<ImageCropMode>(value, out var crop)) return false; img.CropMode = crop; break;
                case "zm": img.Zoom = d(value); break;
                case "fx": img.FocusX = i(value); break;
                case "fy": img.FocusY = i(value); break;
                case "ox": img.OffsetX = i(value); break;
                case "oy": img.OffsetY = i(value); break;
                case "rot": img.Rotation = d(value); break;
                case "bri": img.Brightness = d(value); break;
                case "con": img.Contrast = d(value); break;
                case "sat": img.Saturation = d(value); break;
                case "hue": img.HueShift = d(value); break;
                case "sha": img.Sharpness = d(value); break;
                case "inv": img.InvertLuminance = value == "1"; break;
                case "ald": if (!tryEnumName<AutoLightDarkSwitch>(value, out var ald)) return false; img.AutoLightDarkMode = ald; break;
                case "bg": img.BackgroundColor = "#" + value; break;
                case "abg": img.AutoBackgroundColor = value == "1"; break;
                case "tms": img.TimeOffsetMs = d(value); break;
                case "tpc": img.TimeOffsetPercentage = d(value); break;
                case "tmp": img.Temporary = value == "1"; break;
                case "k": break;
                default: return false;
            }
        }
        return true;

        static int i(string v) => int.Parse(v, CultureInfo.InvariantCulture);
        static double d(string v) => double.Parse(v, CultureInfo.InvariantCulture);
    }
    static bool fmt(string value, FileAdjustment adjustment) {
        if (!tryEnumName<FileFormat>(value, out var format)) return false;
        adjustment.RequestedFormat = format;
        return true;
    }
}
