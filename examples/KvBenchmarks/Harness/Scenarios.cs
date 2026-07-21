using Relatude.DB.Datastores.Indexes.BTreeIndex;
using KvBenchmarks.Engines;

namespace KvBenchmarks.Harness;

public static class Engines
{
    public static readonly string[] All = ["sqlite", "zonetree", "faster", "native",];

    public static string DisplayName(string name) => name switch
    {
        "native" => "NativeKv (B+Tree)",
        "sqlite" => "SQLite",
        "zonetree" => "ZoneTree (LSM)",
        "faster" => "FASTER (+mem idx)",
        _ => name,
    };

    /// <summary>Creates a file-backed engine of the given kind rooted in <paramref name="dir"/>.</summary>
    public static IStorageEngine Create(string name, string dir)
    {
        Directory.CreateDirectory(dir);
        return name switch
        {
            // Same options the production NativeKvIndexStore uses; BENCH_NATIVE_VC overrides the
            // value-cache size for experiments (0 disables it), BENCH_NATIVE_MEM=1 runs memory-only.
            "native" => new BPlusTreeStorageEngine(
                Environment.GetEnvironmentVariable("BENCH_NATIVE_MEM") == "1" ? null : Path.Combine(dir, "native.db"),
                new BPlusTreeEngineOptions
                {
                    PageCacheBytes = 64L * 1024 * 1024,
                    ValueCacheEntries = int.TryParse(Environment.GetEnvironmentVariable("BENCH_NATIVE_VC"), out int vc) ? vc : 1000,
                }),
            "sqlite" => new SqliteEngine(dir),
            "zonetree" => new ZoneTreeEngine(dir),
            "faster" => new FasterEngine(dir),
            _ => throw new ArgumentException($"Unknown engine '{name}'."),
        };
    }

    /// <summary>A memory-only native engine, used as the reference in verification.</summary>
    public static IStorageEngine CreateReference() => new BPlusTreeStorageEngine(null);
}

public abstract class ScenarioBase
{
    public required string Name { get; init; }

    /// <summary>Runs the full benchmark of one engine and returns the measurements.</summary>
    public abstract BenchResult Bench(string engineName, int n, string dir);

    /// <summary>Checks the engine against the native reference; returns null on success or a mismatch description.</summary>
    public abstract string? Verify(string engineName, string dir);
}

public sealed class Scenario<T> : ScenarioBase where T : notnull
{
    /// <summary>Benchmark value distribution; receives the dataset size to scale duplicate rates.</summary>
    public required Func<Random, int, T> Next { get; init; }

    /// <summary>Small closed value pool for verification (drives duplicates and query bounds).</summary>
    public required Func<Random, T[]> VerifyPool { get; init; }

    public override BenchResult Bench(string engineName, int n, string dir)
        => BenchRunner.Run(this, engineName, n, dir);

    public override string? Verify(string engineName, string dir)
        => Verifier.Run(this, engineName, dir);
}

public static class Scenarios
{
    public static readonly ScenarioBase[] All =
    [
        new Scenario<int>
        {
            Name = "int",
            Next = (rnd, n) => rnd.Next(0, Math.Max(8, n / 8)), // ~8 ids per value
            VerifyPool = rnd => Enumerable.Range(0, 40).Select(_ => rnd.Next(-100, 100)).Distinct().ToArray(),
        },
        new Scenario<long>
        {
            Name = "long",
            Next = (rnd, n) => rnd.NextInt64(0, (long)n * 16),
            VerifyPool = rnd => Enumerable.Range(0, 40).Select(_ => rnd.NextInt64(-1_000_000, 1_000_000)).Distinct().ToArray(),
        },
        new Scenario<string>
        {
            Name = "string",
            Next = (rnd, n) => RandomString(rnd, 8 + rnd.Next(17)),
            VerifyPool = rnd => Enumerable.Range(0, 40).Select(_ => RandomString(rnd, 1 + rnd.Next(12))).Distinct().ToArray(),
        },
        new Scenario<Guid>
        {
            Name = "guid",
            Next = (rnd, n) => NextGuid(rnd),
            VerifyPool = rnd => Enumerable.Range(0, 40).Select(_ => NextGuid(rnd)).Distinct().ToArray(),
        },
        new Scenario<DateTime>
        {
            Name = "datetime",
            Next = (rnd, n) => Epoch.AddSeconds(rnd.Next(0, 500_000_000)),
            VerifyPool = rnd => Enumerable.Range(0, 40).Select(_ => Epoch.AddSeconds(rnd.Next(0, 100_000))).Distinct().ToArray(),
        },
    ];

    public static ScenarioBase Get(string name)
        => All.FirstOrDefault(s => s.Name == name) ?? throw new ArgumentException($"Unknown scenario '{name}'.");

    private static readonly DateTime Epoch = new(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private static string RandomString(Random rnd, int length)
    {
        Span<char> chars = stackalloc char[length];
        for (int i = 0; i < length; i++) chars[i] = Alphabet[rnd.Next(Alphabet.Length)];
        return new string(chars);
    }

    private static Guid NextGuid(Random rnd)
    {
        Span<byte> b = stackalloc byte[16];
        rnd.NextBytes(b);
        return new Guid(b);
    }
}
