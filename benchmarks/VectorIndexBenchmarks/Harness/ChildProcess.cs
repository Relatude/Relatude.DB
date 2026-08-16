using System.Diagnostics;
using System.Text.Json;

namespace VectorIndexBenchmarks.Harness;

/// <summary>
/// Runs one engine in a child process — the whole point of which is that the memory columns measure
/// that engine and nothing else — and reads its result back off stdout.
///
/// <para>The protocol is two marked line formats: the child writes exactly one
/// <see cref="ResultMarker"/> line on stdout, its serialized <see cref="BenchResult"/>, and its
/// progress goes the other way on stderr (see <see cref="Progress"/>). Anything else it writes to
/// stderr is a real message and is passed through.</para>
/// </summary>
public static class ChildProcess {
    public const string ResultMarker = "##RESULT## ";

    public static BenchResult Run(string engine, BenchOptions options, string dir, string label) {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add($"--child-engine={engine}");
        psi.ArgumentList.Add($"--child-dir={dir}");
        psi.ArgumentList.Add($"--n={options.N}");
        psi.ArgumentList.Add($"--dims={options.Dimensions}");
        psi.ArgumentList.Add($"--clusters={options.Clusters}");
        psi.ArgumentList.Add($"--noise={options.ClusterNoise}");
        psi.ArgumentList.Add($"--batch={options.BatchSize}");
        psi.ArgumentList.Add($"--cache={options.CacheMB}");
        psi.ArgumentList.Add($"--accuracy={options.Accuracy}");
        psi.ArgumentList.Add($"--hnsw-m={options.HnswConnectivity}");
        psi.ArgumentList.Add($"--hnsw-ef-add={options.HnswExpansionAdd}");
        psi.ArgumentList.Add($"--hnsw-ef={options.HnswExpansionSearch}");
        if (options.MinSimilarity.HasValue) psi.ArgumentList.Add($"--min-sim={options.MinSimilarity.Value}");
        if (options.PersistEveryBatch) psi.ArgumentList.Add("--persist=batch");
        if (options.SkipSearches) psi.ArgumentList.Add("--skip-searches");
        using var proc = Process.Start(psi)!;
        // The child's progress lines, labelled with the engine and rendered over each other; anything
        // else it writes to stderr is a real message and gets a line of its own.
        proc.ErrorDataReceived += (_, e) => {
            if (e.Data is null) return;
            if (Progress.TryUnwrap(e.Data, out var text)) ProgressDisplay.Show($"[{label}] {text}");
            else if (e.Data.Trim().Length > 0) {
                ProgressDisplay.Clear();
                Console.Error.WriteLine(e.Data);
            }
        };
        proc.BeginErrorReadLine();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        var line = stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(ResultMarker));
        return line is null
            ? new BenchResult { Engine = engine, N = options.N, Error = $"child produced no result (exit {proc.ExitCode}): {stdout}" }
            : JsonSerializer.Deserialize<BenchResult>(line[ResultMarker.Length..])!;
    }
}
