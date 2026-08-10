using System.Diagnostics;

namespace VectorIndexBenchmarks.Harness;

/// <summary>
/// Live progress on stderr, so a long run says what it is doing rather than sitting silent for
/// minutes. Results go to stdout and progress to stderr, so a piped run still produces clean tables.
///
/// <para>Each engine runs in its own process, and a child's stderr is read by the parent line by
/// line — so a child sends progress as whole marked lines (<see cref="Marker"/>) that the parent
/// unwraps, labels with the engine name and renders in place. In <c>--in-process</c> mode the
/// parent installs its own sink and the same calls render directly.</para>
/// </summary>
public static class Progress {
    public const string Marker = "##P## ";
    /// <summary>How often a running phase repeats itself. Reporting is a stopwatch read per item,
    /// which is nothing next to the work of the phases that report.</summary>
    const int intervalMs = 150;

    static readonly Stopwatch clock = Stopwatch.StartNew();
    static readonly object gate = new();
    static Action<string> sink = ProgressDisplay.Show;
    static string phase = "";
    static long phaseStartedMs;
    static long nextReportMs;

    /// <summary>Child mode: progress becomes marked lines on stderr for the parent to render.</summary>
    public static void SendToParent() => sink = text => Console.Error.WriteLine(Marker + text);
    /// <summary>Parent mode: where a child's (or an in-process run's) progress is rendered.</summary>
    public static void SendTo(Action<string> destination) => sink = destination;
    /// <summary>Back to rendering the parent's own progress (corpus building) in place.</summary>
    public static void SendToConsole() => sink = ProgressDisplay.Show;

    /// <summary>True when <paramref name="line"/> is a progress line, with its text in <paramref name="text"/>.</summary>
    public static bool TryUnwrap(string line, out string text) {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith(Marker, StringComparison.Ordinal)) {
            text = trimmed[Marker.Length..];
            return true;
        }
        text = "";
        return false;
    }

    /// <summary>Start a phase; reported immediately, then repeated while <see cref="Item"/> is called.</summary>
    public static void Phase(string name) {
        lock (gate) {
            phase = name;
            phaseStartedMs = clock.ElapsedMilliseconds;
            nextReportMs = phaseStartedMs + intervalMs;
            sink(name);
        }
    }

    /// <summary>Progress within the current phase. Cheap to call in a tight loop: everything past
    /// the elapsed-time check happens a few times a second.</summary>
    public static void Item(long done, long total) {
        if (clock.ElapsedMilliseconds < nextReportMs) return;
        lock (gate) {
            var now = clock.ElapsedMilliseconds;
            if (now < nextReportMs) return; // another thread reported while this one waited
            nextReportMs = now + intervalMs;
            var seconds = (now - phaseStartedMs) / 1000.0;
            sink(total > 0 ? $"{phase} {100.0 * done / total:0}% ({seconds:0.0}s)" : $"{phase} ({seconds:0.0}s)");
        }
    }
}

/// <summary>
/// Renders one line of progress, overwriting the previous one so a run occupies a single line. A
/// redirected stderr has no cursor to move, so there it degrades to an occasional plain line
/// instead of thousands of carriage returns in a log file.
/// </summary>
public static class ProgressDisplay {
    /// <summary>How often a redirected stderr gets a line, since it cannot be overwritten.</summary>
    const int redirectedIntervalMs = 5_000;

    static readonly bool inPlace = !Console.IsErrorRedirected;
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static long nextRedirectedMs;
    static int width;

    public static void Show(string text) {
        if (!inPlace) {
            if (clock.ElapsedMilliseconds < nextRedirectedMs) return;
            nextRedirectedMs = clock.ElapsedMilliseconds + redirectedIntervalMs;
            Console.Error.WriteLine(text);
            return;
        }
        Console.Error.Write('\r' + text.PadRight(width));
        width = Math.Max(width, text.Length);
    }

    /// <summary>Erase the progress line, so whatever is written next starts on a clean one.</summary>
    public static void Clear() {
        if (!inPlace) {
            nextRedirectedMs = 0; // the next phase of the next engine is worth a line whenever it comes
            return;
        }
        if (width > 0) Console.Error.Write('\r' + new string(' ', width) + '\r');
        width = 0;
    }
}
