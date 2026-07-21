namespace KvBenchmarks.Engines;

/// <summary>
/// Benchmark-only hook: forces everything written so far onto disk so the loaded state is
/// really persisted before it is measured, and so read phases exercise the engine's disk
/// path instead of a write buffer that never spilled. Engines whose durable commit already
/// does this (native, SQLite, FASTER) don't implement it.
/// </summary>
public interface IBenchFlush
{
    void FlushAllToDisk();
}
