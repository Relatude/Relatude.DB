using System.Globalization;
using System.Text.Json.Nodes;

namespace Relatude.DB.FileConversion;

/// <summary>
/// What ffprobe tells of a media file, read from its JSON: how long it lasts, the size of its video,
/// whether it has sound and what its container is. <see cref="Json"/> is all of it, compact and without
/// the path of the file probed - a temp file named anew for every probe.
/// </summary>
internal sealed class MediaProbe {
    public TimeSpan Duration { get; private init; }
    /// <summary>The width of the first video stream, 0 without one.</summary>
    public int Width { get; private init; }
    /// <summary>The height of the first video stream, 0 without one.</summary>
    public int Height { get; private init; }
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
        return new MediaProbe {
            Duration = durations.Max(),
            Width = integer(video?["width"]),
            Height = integer(video?["height"]),
            HasAudio = audio != null,
            FormatLongName = text(format?["format_long_name"]) ?? string.Empty,
            Json = root.ToJsonString(),
        };
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
