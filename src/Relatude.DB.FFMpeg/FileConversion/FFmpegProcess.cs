using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Relatude.DB.FileConversion;

/// <summary>
/// Runs ffmpeg and ffprobe as processes: the binaries in <see cref="BinaryFolder"/>, or the ones on the
/// PATH when a binary is not there, as when the download failed. Arguments are passed one by one, so
/// paths need no quoting. A run that fails throws with the last error lines ffmpeg wrote.
/// </summary>
internal static class FFmpegProcess {
    /// <summary>The folder the downloader puts ffmpeg and ffprobe in.</summary>
    public static string? BinaryFolder { get; set; }
    const int _errorLinesKept = 12;

    /// <summary>
    /// Runs ffmpeg with arguments that name the input and the output; an output already there is
    /// overwritten. Given the duration of the output and a callback, it reports how far it has come, as a
    /// percentage. Cancelling kills ffmpeg and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static Task RunAsync(IEnumerable<string> arguments, CancellationToken ct = default, TimeSpan? duration = null, Action<double>? onPercent = null) {
        List<string> all = ["-hide_banner", "-nostdin", "-loglevel", "error", "-y"];
        Action<string>? onOutputLine = null;
        if (onPercent != null && duration > TimeSpan.Zero) {
            // key=value lines on stdout, where out_time_us is how much of the output is written
            all.AddRange(["-progress", "pipe:1", "-nostats"]);
            var totalUs = duration.Value.Ticks / 10.0;
            onOutputLine = line => {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                    onPercent(Math.Min(100, us / totalUs * 100));
            };
        }
        all.AddRange(arguments);
        return runAsync("ffmpeg", all, onOutputLine, ct);
    }
    /// <summary><see cref="RunAsync"/>, waited for.</summary>
    public static void Run(IEnumerable<string> arguments) => RunAsync(arguments).GetAwaiter().GetResult();

    /// <summary>What ffprobe tells of a media file: its container, streams and chapters.</summary>
    public static async Task<MediaProbe> ProbeAsync(string path, CancellationToken ct = default) {
        var json = new StringBuilder();
        await runAsync("ffprobe", ["-hide_banner", "-loglevel", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", path],
            line => json.AppendLine(line), ct);
        return MediaProbe.Parse(json.ToString());
    }

    static async Task runAsync(string name, List<string> arguments, Action<string>? onOutputLine, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(binaryPath(name)) {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = psi };
        try {
            process.Start();
        } catch (Exception ex) {
            throw new InvalidOperationException($"Could not start {name} ({psi.FileName}): {ex.Message}", ex);
        }
        var errorLines = new Queue<string>();
        // both pipes are read to the end: ffmpeg stops when one it writes to is full
        var readOutput = readLinesAsync(process.StandardOutput, line => onOutputLine?.Invoke(line));
        var readErrors = readLinesAsync(process.StandardError, line => {
            if (string.IsNullOrWhiteSpace(line)) return;
            errorLines.Enqueue(line.Trim());
            if (errorLines.Count > _errorLinesKept) errorLines.Dequeue();
        });
        using (ct.Register(() => kill(process))) {
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(readOutput, readErrors);
        }
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) {
            var errors = errorLines.Count > 0 ? string.Join(Environment.NewLine, errorLines) : "no error output";
            throw new InvalidOperationException($"{name} failed (exit code {process.ExitCode}): {errors}");
        }
    }
    static async Task readLinesAsync(StreamReader reader, Action<string> onLine) {
        Exception? failed = null;
        while (await reader.ReadLineAsync() is { } line) {
            if (failed != null) continue; // still read, or ffmpeg waits for the pipe forever
            try { onLine(line); } catch (Exception ex) { failed = ex; }
        }
        if (failed != null) ExceptionDispatchInfo.Throw(failed);
    }
    static void kill(Process process) {
        try { process.Kill(entireProcessTree: true); } catch { } // it has exited
    }
    static string binaryPath(string name) {
        if (BinaryFolder is { } folder) {
            var path = Path.Combine(folder, OperatingSystem.IsWindows() ? name + ".exe" : name);
            if (File.Exists(path)) return path;
        }
        return name; // the one on the PATH
    }
}
