namespace Relatude.DB.IO;

public readonly record struct IOBenchmarkProgress(double Percent, string Step);

public static class IOBenchmarkGroups {
    public const string Write = "write";
    public const string Durable = "durable";
    public const string Sequential = "sequential";
    public const string Scattered = "scattered";
    public const string Uncached = "uncached";
    public const string Files = "files";
    public static string TitleOf(string group) => group switch {
        Write => "Writes",
        Durable => "Durable writes",
        Sequential => "Sequential reads",
        Scattered => "Scattered reads",
        Uncached => "Reads past the cache",
        Files => "File operations",
        _ => group,
    };
}

public sealed class IOBenchmarkLatency {
    public long Samples { get; init; }
    public double MinUs { get; init; }
    public double MeanUs { get; init; }
    public double P50Us { get; init; }
    public double P90Us { get; init; }
    public double P99Us { get; init; }
    public double MaxUs { get; init; }
}

public sealed class IOBenchmarkTestResult {
    public string Id { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int BlockSize { get; init; }
    public long Operations { get; set; }
    public long Bytes { get; set; }
    public double ElapsedMs { get; set; }
    public double BytesPerSecond { get; set; }
    public double OperationsPerSecond { get; set; }
    public IOBenchmarkLatency? Latency { get; set; }
    public IOCounters? Storage { get; set; }
    public long Verified { get; set; }
    public long Mismatches { get; set; }
    public string? Error { get; set; }
}

public sealed class IOBenchmarkReport {
    public string Kind { get; set; } = string.Empty;
    public string? Location { get; set; }
    public DateTime StartedUtc { get; set; }
    public double ElapsedMs { get; set; }
    public double PreparationMs { get; set; }
    public int TestDurationMs { get; set; }
    public long FileBytes { get; set; }
    public double TimerResolutionUs { get; set; }
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
    public List<IOBenchmarkTestResult> Tests { get; } = [];
    public List<string> Notes { get; } = [];
}
