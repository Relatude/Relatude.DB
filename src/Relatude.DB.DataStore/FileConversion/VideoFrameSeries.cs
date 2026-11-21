using System.Collections.Concurrent;
using System.Globalization;
using Relatude.DB.Common;
using Relatude.DB.FileConversion.FFMpeg;
using Relatude.DB.FileConversion.ImageEncoders;

namespace Relatude.DB.FileConversion;

/// <summary>How a series of a video's frames is made: how many, how large, and whether by decoding the video once.</summary>
internal sealed record FrameSeriesPlan(double Duration, int Count, int Width, int Height, bool Decode);

/// <summary>
/// A series of a video's frames (<see cref="FileAdjustmentImage.Frames"/>) as an animation, each frame shown until the
/// next one's time. What makes it fast is the keyframes: a keyframe decodes alone, any other frame only after every
/// frame back to the keyframe before it (often seconds of video), and a video decodes fastest straight through. So the
/// frames are the keyframes before even times (<see cref="FileAdjustmentImage.KeyframesOnly"/>, and any video too long
/// to decode whole), each grabbed by an ffmpeg run of its own, several at once, coarse to fine, a time skipped when a
/// grab before it showed its keyframe; or the video decoded once, a frame kept at each even time. Measured on a desktop
/// of 2024 (16 cores): ten keyframes of a 22 s 4K video in 0.65 s, where seeking exactly to ten times took 5.4 s, and
/// 120 even frames of it decoded in 2 s; slicing the decoding into parallel runs was slower, as each slice decodes part
/// of a keyframe interval again. Every frame's time is its file's name (<c>-frame_pts</c> in milliseconds from the
/// start of the file, with <c>-fps_mode passthrough</c>, without which a millisecond time base repeats each frame for
/// every millisecond).
/// </summary>
internal static class VideoFrameSeries {
    /// <summary>The longer side of a frame when the adjustment gives no size.</summary>
    public const int DefaultBox = 384;
    /// <summary>The video decoded once up to so many pixels decoded (seconds × frames a second × width × height):
    /// about 4 s on that desktop (a 22 s 4K video is 6e9, a 218 s 1080p60 one 27e9).</summary>
    public const double DecodeBudget = 12e9;
    /// <summary>The pixels of every frame together (32 M, 128 MB as the animation holds them): frames are made smaller past it.</summary>
    public const long MaxPixels = 32L << 20;
    const int grabsAtOnce = 4;
    const int defaultQuality = 75;

    static string n(double v) => Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);

    public static FrameSeriesPlan Plan(double duration, double frameRate, int videoWidth, int videoHeight, FileAdjustmentImage adj) {
        var d = double.IsFinite(duration) && duration > 0 ? duration : 0;
        var fps = double.IsFinite(frameRate) && frameRate > 0 ? frameRate : 30;
        var count = Math.Clamp(adj.Frames ?? 1, 1, FileAdjustmentImage.MaxFrames);
        count = d > 0 ? Math.Min(count, Math.Max(1, (int)Math.Floor(d * fps))) : 1; // never more frames than the video has
        // the frame: within the size asked for, the video's shape, never enlarged
        double vw = videoWidth > 0 ? videoWidth : 16 * DefaultBox / 9.0, vh = videoHeight > 0 ? videoHeight : DefaultBox;
        var scale = adj.Width == null && adj.Height == null
            ? Math.Min(1, DefaultBox / Math.Max(vw, vh))
            : Math.Min(1, Math.Min(adj.Width is int w ? w / vw : double.MaxValue, adj.Height is int h ? h / vh : double.MaxValue));
        var total = count * vw * scale * vh * scale;
        if (total > MaxPixels) scale *= Math.Sqrt(MaxPixels / total);
        var width = Math.Max(2, (int)Math.Round(vw * scale));
        var height = Math.Max(2, (int)Math.Round(vh * scale));
        var decode = adj.KeyframesOnly != true && d > 0 && count > 1 && d * fps * vw * vh <= DecodeBudget;
        return new(d, count, width, height, decode);
    }

    /// <summary><paramref name="count"/> times across a video, each in the middle of its share.</summary>
    public static double[] Times(double duration, int count) =>
        [.. Enumerable.Range(0, count).Select(i => duration * (i + 0.5) / count)];

    /// <summary>The indexes 0 to <paramref name="count"/> - 1 coarse to fine: the middle, then the middles of the halves, and so on.</summary>
    public static IEnumerable<int> CoarseToFine(int count) {
        var queue = new Queue<(int From, int To)>();
        queue.Enqueue((0, count));
        while (queue.Count > 0) {
            var (from, to) = queue.Dequeue();
            if (from >= to) continue;
            var middle = (from + to) / 2;
            yield return middle;
            queue.Enqueue((from, middle));
            queue.Enqueue((middle + 1, to));
        }
    }

    /// <summary>
    /// Whether the keyframe before <paramref name="time"/> is known: a grab at a time (To) gave the keyframe at From, so
    /// there is none between them, and any time from From to To gives that one again.
    /// </summary>
    public static bool Known(IEnumerable<(double From, double To)> grabbed, double time) => grabbed.Any(g => g.From <= time && time <= g.To);

    static string scale(int width, int height) => string.Create(CultureInfo.InvariantCulture, $"scale={width}:{height}:flags=bicubic,setsar=1");

    /// <summary>The keyframe at or before <paramref name="at"/>, alone, into <paramref name="pattern"/> (with %d), named by its time in milliseconds.</summary>
    public static List<string> Grab(string input, double at, int width, int height, string pattern) => [
        "-skip_frame", "nokey", "-noaccurate_seek", "-ss", n(Math.Max(0, at)), "-copyts", "-start_at_zero", "-i", input,
        "-map", "0:v:0", "-frames:v", "1", "-vf", scale(width, height),
        "-enc_time_base", "1/1000", "-fps_mode", "passthrough", "-frame_pts", "1", "-f", "image2", pattern,
    ];

    /// <summary>The whole video decoded once, a frame kept every 1 / <paramref name="rate"/> seconds, into <paramref name="pattern"/>, named by their times.</summary>
    public static List<string> Decode(string input, double rate, int width, int height, string pattern) => [
        "-copyts", "-start_at_zero", "-i", input,
        "-map", "0:v:0", "-vf", "fps=" + n(rate) + "," + scale(width, height),
        "-enc_time_base", "1/1000", "-fps_mode", "passthrough", "-frame_pts", "1", "-f", "image2", pattern,
    ];

    /// <summary>The time in milliseconds a frame's file is named with (prefix_milliseconds.bmp); null for any other file.</summary>
    public static long? TimeOf(string path) {
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)) return null;
        var stem = Path.GetFileNameWithoutExtension(name);
        var at = stem.LastIndexOf('_');
        return at >= 0 && long.TryParse(stem.AsSpan(at + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms >= 0 ? ms : null;
    }

    /// <summary>
    /// How long each frame is shown, in milliseconds, so that each starts at its time: the first at 0 whatever its own
    /// time (a video's first frame), the last until the video's end. At least 1 each. <paramref name="each"/> instead
    /// shows every frame that long (<see cref="FileAdjustmentImage.FrameDuration"/>).
    /// </summary>
    public static int[] Durations(IReadOnlyList<long> times, double duration, int? each = null) {
        if (each is int ms) return [.. times.Select(_ => ms)];
        var durations = new int[times.Count];
        for (var i = 0; i < times.Count; i++) {
            var start = i == 0 ? 0 : times[i];
            var end = i + 1 < times.Count ? times[i + 1] : Math.Max(start + 100, (long)Math.Round(duration * 1000));
            durations[i] = (int)Math.Clamp(end - start, 1, 0xFFFFFF);
        }
        return durations;
    }

    /// <summary>
    /// Makes the series of the video at <paramref name="input"/> into <paramref name="output"/>, an animated WebP or GIF,
    /// telling <paramref name="report"/> how far it has come (0 to 100) and what it does. Temporary files go into a
    /// folder of their own below <paramref name="tempRoot"/>, removed at the end.
    /// </summary>
    public static async Task MakeAsync(string input, string output, FileFormat format, FileAdjustmentImage adj, MediaProbe probe,
        string tempRoot, Action<int, string> report, CancellationToken ct) {
        if (format is not (FileFormat.Webp or FileFormat.Gif)) throw new NotSupportedException("A series of frames is an animation: WebP or GIF, not " + format.ToString().ToUpperInvariant() + ". ");
        if (!probe.HasVideo) throw new InvalidOperationException("The file has no picture. ");
        var plan = Plan(probe.Duration.TotalSeconds, probe.FrameRate, probe.DisplayWidth, probe.DisplayHeight, adj);
        var folder = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            var frames = new ConcurrentDictionary<long, string>();
            if (plan.Decode) {
                report(5, "Decoding the video");
                await FFmpegProcess.RunAsync(Decode(input, plan.Count / plan.Duration, plan.Width, plan.Height, Path.Combine(folder, "d_%d.bmp")), ct,
                    TimeSpan.FromSeconds(plan.Duration), percent => report(5 + (int)(percent * 0.8), "Decoding the video"));
                foreach (var path in Directory.EnumerateFiles(folder, "d_*")) if (TimeOf(path) is { } ms) frames.TryAdd(ms, path);
            } else {
                var times = plan.Duration > 0 ? Times(plan.Duration, plan.Count) : [0.0];
                var grabbed = new List<(double From, double To)>();
                var done = 0;
                await Parallel.ForEachAsync(CoarseToFine(times.Length), new ParallelOptions { MaxDegreeOfParallelism = grabsAtOnce, CancellationToken = ct }, async (i, token) => {
                    bool known;
                    lock (grabbed) known = Known(grabbed, times[i]);
                    if (!known) {
                        var prefix = "k" + i.ToString(CultureInfo.InvariantCulture);
                        await FFmpegProcess.RunAsync(Grab(input, times[i], plan.Width, plan.Height, Path.Combine(folder, prefix + "_%d.bmp")), token);
                        foreach (var path in Directory.EnumerateFiles(folder, prefix + "_*")) {
                            if (TimeOf(path) is not { } ms) continue;
                            lock (grabbed) grabbed.Add((ms / 1000.0, times[i]));
                            frames.TryAdd(ms, path); // the same keyframe from another time is kept once
                        }
                    }
                    report(5 + 80 * Interlocked.Increment(ref done) / times.Length, "Taking keyframes");
                });
            }
            if (frames.IsEmpty) throw new InvalidOperationException("FFmpeg took no frame from the video. ");
            report(88, "Writing the animation");
            var ordered = frames.OrderBy(f => f.Key).ToArray();
            var pictures = new InternalImage[ordered.Length];
            for (var i = 0; i < ordered.Length; i++) {
                ct.ThrowIfCancellationRequested();
                var picture = InternalImage.Load(ordered[i].Value);
                // every frame is the canvas's size; a frame ffmpeg made otherwise (it should not) is made so
                pictures[i] = picture.Width == plan.Width && picture.Height == plan.Height ? picture : picture.Resize(plan.Width, plan.Height);
            }
            var animation = new Animation(pictures, Durations([.. ordered.Select(f => f.Key)], plan.Duration, adj.FrameDuration), 0);
            await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            // whole frames, so a reader takes any one alone: a browser shows a frame as a still WebP, with no canvas to draw it on
            if (format == FileFormat.Webp) WebpCodec.WriteAnimation(animation, stream, new ImageSaveOptions { Quality = adj.Quality ?? defaultQuality }, wholeFrames: true);
            else GifCodec.Write(animation, stream);
            report(99, "Writing the animation");
        } finally {
            try { Directory.Delete(folder, recursive: true); } catch { } // the engine clears what is left in its temp folder
        }
    }
}
