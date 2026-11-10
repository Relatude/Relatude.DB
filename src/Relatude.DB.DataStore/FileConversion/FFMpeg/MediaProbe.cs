using System.Globalization;
using System.Text.Json.Nodes;

namespace Relatude.DB.FileConversion.FFMpeg;

/// <summary>
/// What ffprobe tells of a media file, read from its JSON: how long it lasts, the size of its video, whether it has
/// sound and what its container is. <see cref="Json"/> is all of it, compact and without the path of the file probed -
/// a temp file named anew for every probe.
/// </summary>
public sealed class MediaProbe {
    public TimeSpan Duration { get; private init; }
    /// <summary>The width of the first video stream as stored, 0 without one.</summary>
    public int Width { get; private init; }
    /// <summary>The height of the first video stream as stored, 0 without one.</summary>
    public int Height { get; private init; }
    /// <summary>
    /// How far the first video stream is turned to be shown, clockwise in degrees: 0, 90, 180 or 270, from its display
    /// matrix or its rotate tag. ffmpeg turns it so before any filter, as players do.
    /// </summary>
    public int Rotation { get; private init; }
    /// <summary>The width of the picture as shown: the stored height when it is turned a quarter.</summary>
    public int DisplayWidth => Rotation is 90 or 270 ? Height : Width;
    /// <summary>The height of the picture as shown: the stored width when it is turned a quarter.</summary>
    public int DisplayHeight => Rotation is 90 or 270 ? Width : Height;
    /// <summary>Frames a second of the first video stream (its average, else its base rate); 0 without one.</summary>
    public double FrameRate { get; private init; }
    /// <summary>The codec of the first video stream, such as "h264"; null without one.</summary>
    public string? VideoCodec { get; private init; }
    /// <summary>The codec of the first sound stream, such as "aac"; null without one.</summary>
    public string? AudioCodec { get; private init; }
    public bool HasVideo => Width > 0 && Height > 0;
    public bool HasAudio { get; private init; }
    public string FormatLongName { get; private init; } = string.Empty;
    public string Json { get; private init; } = string.Empty;

    public static MediaProbe Parse(string ffprobeJson) {
        var root = JsonNode.Parse(ffprobeJson) as JsonObject ?? throw new InvalidOperationException("ffprobe gave no description of the file. ");
        var format = root["format"] as JsonObject;
        format?.Remove("filename");
        var streams = (root["streams"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        // the cover art of a recording is a video stream too: a picture, not the video
        var video = streams.FirstOrDefault(s => text(s["codec_type"]) == "video" && integer(s["disposition"]?["attached_pic"]) != 1);
        var audio = streams.FirstOrDefault(s => text(s["codec_type"]) == "audio");
        // the longest of the container and its first video and sound, as FFMpegCore measured it
        TimeSpan[] durations = [seconds(text(format?["duration"])), streamDuration(video), streamDuration(audio)];
        var rate = ratio(text(video?["avg_frame_rate"]));
        if (rate <= 0 || rate > 1000) rate = ratio(text(video?["r_frame_rate"]));
        return new MediaProbe {
            Duration = durations.Max(),
            Width = integer(video?["width"]),
            Height = integer(video?["height"]),
            Rotation = rotationOf(video),
            FrameRate = rate is > 0 and <= 1000 ? Math.Round(rate, 3) : 0,
            VideoCodec = text(video?["codec_name"]),
            AudioCodec = text(audio?["codec_name"]),
            HasAudio = audio != null,
            FormatLongName = text(format?["format_long_name"]) ?? string.Empty,
            Json = root.ToJsonString(),
        };
    }
    /// <summary>
    /// The turn a player gives the stream, clockwise: a display matrix's rotation is counterclockwise (-90 is a quarter
    /// clockwise), an old rotate tag clockwise.
    /// </summary>
    static int rotationOf(JsonObject? stream) {
        if (stream == null) return 0;
        double? degrees = null;
        foreach (var data in (stream["side_data_list"] as JsonArray)?.OfType<JsonObject>() ?? []) {
            if (data["rotation"] is JsonValue value && number(value) is { } r) { degrees = -r; break; }
        }
        if (degrees == null && (stream["tags"] as JsonObject)?["rotate"] is JsonValue tag && number(tag) is { } rotate) degrees = rotate;
        if (degrees == null) return 0;
        var quarter = (int)Math.Round(degrees.Value / 90.0);
        return ((quarter % 4) + 4) % 4 * 90;
    }
    static double? number(JsonValue value) {
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }
    /// <summary>"30000/1001": a frame rate as ffprobe writes it.</summary>
    static double ratio(string? value) {
        if (string.IsNullOrEmpty(value)) return 0;
        var parts = value.Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return 0;
        if (parts.Length == 1) return n;
        return double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? n / d : 0;
    }
    /// <summary>The stream's own duration, or the one in its tags, which is where Matroska and WebM keep it.</summary>
    static TimeSpan streamDuration(JsonObject? stream) {
        if (stream == null) return TimeSpan.Zero;
        var duration = seconds(text(stream["duration"]));
        if (duration > TimeSpan.Zero) return duration;
        var tags = stream["tags"] as JsonObject;
        return clock(text(tags?["DURATION"] ?? tags?["duration"]));
    }
    /// <summary>"5.008000": seconds, as ffprobe writes durations.</summary>
    static TimeSpan seconds(string? value) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var s) ? TimeSpan.FromTicks((long)(s * TimeSpan.TicksPerSecond)) : TimeSpan.Zero;
    /// <summary>"00:00:05.008000000": hours, minutes and seconds, as Matroska tags write durations.</summary>
    static TimeSpan clock(string? value) {
        var parts = value?.Split(':');
        if (parts is not { Length: 3 }
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            || !decimal.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var s)) return TimeSpan.Zero;
        return TimeSpan.FromTicks((long)((h * 3600 + m * 60 + s) * TimeSpan.TicksPerSecond));
    }
    static string? text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
    static int integer(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var i) ? i : 0;
}
