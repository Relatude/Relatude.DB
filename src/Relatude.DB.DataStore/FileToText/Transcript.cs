using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Relatude.DB.FileToText;

/// <summary>A line of a transcript: when it is spoken, from its start to its end, and what is said.</summary>
public sealed record TranscriptCue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// The text the FileToText service gives for a recording or the sound of a video
/// (<see cref="FileToTextResult.Timed"/>): a line for each stretch of speech, beginning with when it
/// starts and ends - in square brackets, in the time format WebVTT uses - and then what is said:
/// <code>
///   [00:01:02.500 --> 00:01:05.250] And that is how it began.
///   [00:01:05.900 --> 00:01:08.000] Nobody saw it coming.
/// </code>
/// <para>What follows the brackets is what an index wants: <see cref="RemoveTimestamps"/> takes the
/// brackets away and leaves a line of speech for each line. The lines are also a video player's
/// subtitles: <see cref="ToWebVtt"/> and <see cref="ToSrt"/> write them as the two formats players
/// read. The service cuts a long stretch into lines a subtitle holds, two of 42 characters at most
/// and seven seconds.</para>
/// </summary>
public static class Transcript {
    // the hours are as many as there are, the rest two digits each and three for the milliseconds
    const string _time = @"(\d{2,}):([0-5]\d):([0-5]\d)\.(\d{3})";
    static readonly Regex _timestamps = new(@"^\[" + _time + " --> " + _time + @"\] ?", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>Whether the text is a transcript: its first line that is not empty begins with the time it is spoken.</summary>
    public static bool IsTimed(string? text) {
        if (string.IsNullOrEmpty(text)) return false;
        var line = text.TrimStart().Split('\n', 2)[0];
        return _timestamps.IsMatch(line);
    }

    /// <summary>
    /// The text without the time at the start of each line: what is said, a line for each stretch of
    /// it, as an index takes it. Text that is not a transcript comes back as it is.
    /// </summary>
    public static string RemoveTimestamps(string text) => _timestamps.Replace(text ?? "", "");

    /// <summary>
    /// The lines of a transcript. A line that does not begin with a time goes with the line before it,
    /// and text before the first time is left out.
    /// </summary>
    public static IReadOnlyList<TranscriptCue> Parse(string? text) {
        var cues = new List<TranscriptCue>();
        foreach (var raw in (text ?? "").Split('\n')) {
            var line = raw.TrimEnd('\r');
            var match = _timestamps.Match(line);
            if (match.Success) {
                cues.Add(new TranscriptCue(time(match, 1), time(match, 5), line[match.Length..].Trim()));
            } else if (cues.Count > 0 && line.Trim().Length > 0) {
                var last = cues[^1];
                cues[^1] = last with { Text = (last.Text + " " + line.Trim()).Trim() };
            }
        }
        return cues;
    }

    static TimeSpan time(Match match, int group) {
        long part(int i) => long.Parse(match.Groups[group + i].Value, CultureInfo.InvariantCulture);
        return TimeSpan.FromHours(part(0)) + TimeSpan.FromMinutes(part(1)) + TimeSpan.FromSeconds(part(2)) + TimeSpan.FromMilliseconds(part(3));
    }

    /// <summary>hh:mm:ss.fff, with as many hours as there are: the time of a transcript, and of WebVTT.</summary>
    public static string Format(TimeSpan time) {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return string.Create(CultureInfo.InvariantCulture, $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
    }

    /// <summary>A line of a transcript, as the service writes it.</summary>
    public static string Line(TranscriptCue cue) => $"[{Format(cue.Start)} --> {Format(cue.End)}] {oneLine(cue.Text)}";

    /// <summary>The lines of a transcript, as the service writes them: one for each cue, separated by \n.</summary>
    public static string Write(IEnumerable<TranscriptCue> cues) => string.Join("\n", cues.Select(Line));

    /// <summary>The cues moved this much later: the lines of a part of a recording, timed from the start of the whole.</summary>
    public static IEnumerable<TranscriptCue> Shift(IEnumerable<TranscriptCue> cues, TimeSpan by) =>
        cues.Select(c => c with { Start = c.Start + by, End = c.End + by });

    /// <summary>
    /// The cues as WebVTT, the subtitles HTML video players read (a &lt;track&gt; with kind="subtitles"
    /// or "captions"): text/vtt, in UTF-8. The text is escaped as WebVTT wants it.
    /// </summary>
    public static string ToWebVtt(IEnumerable<TranscriptCue> cues) {
        var vtt = new StringBuilder("WEBVTT\n");
        foreach (var cue in cues) {
            vtt.Append('\n').Append(Format(cue.Start)).Append(" --> ").Append(Format(end(cue))).Append('\n');
            vtt.Append(oneLine(cue.Text).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")).Append('\n');
        }
        return vtt.ToString();
    }

    /// <summary>The cues as SubRip (.srt), which most players and editors read: numbered, with a comma before the milliseconds.</summary>
    public static string ToSrt(IEnumerable<TranscriptCue> cues) {
        var srt = new StringBuilder();
        var number = 0;
        foreach (var cue in cues) {
            if (number > 0) srt.Append('\n');
            srt.Append(++number).Append('\n');
            srt.Append(Format(cue.Start).Replace('.', ',')).Append(" --> ").Append(Format(end(cue)).Replace('.', ',')).Append('\n');
            srt.Append(oneLine(cue.Text)).Append('\n');
        }
        return srt.ToString();
    }

    /// <summary>A cue ends when it starts at the earliest: a player skips one that ends before it starts.</summary>
    static TimeSpan end(TranscriptCue cue) => cue.End < cue.Start ? cue.Start : cue.End;

    /// <summary>The text on one line, its runs of white space one space: a line break would end the cue.</summary>
    static string oneLine(string text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
