using System.Globalization;

namespace Relatude.DB.IO;

public sealed record IOBenchmarkOptions {
    public const int KB = 1024;
    public const int MB = 1024 * 1024;
    public string Kind { get; init; } = "Storage";
    public string? Location { get; init; }
    public string? CannotRun { get; init; }
    public long ReadFileBytes { get; init; } = 32L * MB;
    public long RecycleFileBytes { get; init; } = 32L * MB;
    public long DurableFlushBytes { get; init; } = 8L * MB;
    public bool MeasureDurableWrites { get; init; } = true;
    public int[] WriteSizes { get; init; } = [64, 512, 4 * KB, 64 * KB, MB, 4 * MB];
    public int[] CommitSizes { get; init; } = [256, 4 * KB, 64 * KB];
    public int DurableStreamSize { get; init; } = MB;
    public int[] SequentialReadSizes { get; init; } = [4 * KB, 64 * KB, MB];
    public int[] ScatteredReadSizes { get; init; } = [64, 512, 4 * KB, 64 * KB, MB];
    public IReadOnlyList<IOBenchmarkCustomTest> CustomTests { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];

    public static double DurationScale(int testDurationInMs, double max = 12) => Math.Clamp(testDurationInMs / 5000.0, 1, max);
    public static long ScaledBytes(long bytesAtFiveSeconds, int testDurationInMs, long max) =>
        Math.Min(max, (long)(bytesAtFiveSeconds * DurationScale(testDurationInMs)) / MB * MB);
    public static string SizeLabel(long bytes) => bytes switch {
        >= MB when bytes % MB == 0 => bytes / MB + " MB",
        >= KB when bytes % KB == 0 => bytes / KB + " KB",
        _ => bytes + " B",
    };
    public static string Readable(double bytes) {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) {
            bytes /= 1024;
            unit++;
        }
        return (unit == 0 || bytes >= 100 ? Math.Round(bytes).ToString("0", CultureInfo.InvariantCulture) : bytes.ToString("0.0", CultureInfo.InvariantCulture)) + " " + units[unit];
    }
}

public sealed record IOBenchmarkCustomTest(string Id, string Group, string Name, string Description, int BlockSize, double Weight, Action<IOBenchmarkContext> Run);

public delegate bool IOBenchmarkReadFileCheck(ReadOnlySpan<byte> data, long position);

public sealed class IOBenchmarkContext {
    readonly IOBenchmarkReadFileCheck _matches;
    internal IOBenchmarkContext(IIOProvider io, string[] readFileKey, long readFileBytes, IOBenchmarkMeter meter, Random random,
        IOBenchmarkReadFileCheck matches, CancellationToken cancellation) {
        Io = io;
        ReadFileKey = readFileKey;
        ReadFileBytes = readFileBytes;
        Meter = meter;
        Random = random;
        _matches = matches;
        Cancellation = cancellation;
    }
    public IIOProvider Io { get; }
    public string[] ReadFileKey { get; }
    public long ReadFileBytes { get; }
    public IOBenchmarkMeter Meter { get; }
    public Random Random { get; }
    public CancellationToken Cancellation { get; }
    public bool MatchesReadFile(ReadOnlySpan<byte> data, long position) => _matches(data, position);
}
