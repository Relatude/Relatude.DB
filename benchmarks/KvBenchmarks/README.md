# KvBenchmarks

Benchmarks the internal **NativeKvStore** (`BPlusTreeStorageEngine` in `Relatude.DB.KvStore`)
against `ISortedIndex<T>` / `IStorageEngine` implementations built on three external stores,
all implemented inside this project:

| Engine | File | Approach |
|---|---|---|
| NativeKv | (project reference) | Copy-on-write B+Tree with shadow paging, 64 MB page cache (same options as the production `NativeKvIndexStore`) |
| SQLite | `Engines/SqliteEngine.cs` | One table per index `(id INTEGER PRIMARY KEY, v ...)` plus a covering `(v, id)` index; WAL mode, `synchronous=FULL`, cached prepared statements |
| ZoneTree | `Engines/ZoneTreeEngine.cs` | LSM tree; two ZoneTrees per index: `id → value` and a composite `(value bytes + id) → ()` tree for ordered scans |
| FASTER | `Engines/FasterEngine.cs` | FasterKV (`SpanByte`) for persistence and point lookups + an in-memory `SortedSet` of composite `(value, id)` keys for every ordered operation (FASTER is a hash store and has no ordered scans) |

The ZoneTree and FASTER engines share an order-preserving, prefix-free key codec
(`Codecs.cs`) that mirrors the native engine's `KeyCodec`, so all engines sort identically
(ordinal strings, IEEE-754 total order doubles, big-endian RFC-4122 Guids, ...).

## Running

```
dotnet run -c Release --project examples/KvBenchmarks
```

Options (all optional):

```
--n=100000                                entries per scenario (default 100k)
--engines=native,sqlite,zonetree,faster   subset of engines
--scenarios=int,long,string,guid,datetime subset of value-type scenarios
--data=<dir>                              where store files are created (default %TEMP%)
--no-verify                               skip the correctness pass
--in-process                              don't isolate runs in child processes
```

## What it does

1. **Verification** — every candidate engine replays an identical random op stream next to
   the native engine (the reference) and every `ISortedIndex<T>` query — point lookups,
   ascending/descending range scans with all inclusive/exclusive bound combinations, counts,
   `Keys`/`Entries`/`DistinctValues`, min/max — is compared after each commit and sometimes
   mid-transaction. A mismatch aborts the benchmark: numbers from a wrong index are meaningless.
2. **Benchmark phases** per engine × scenario, each in its own child process so memory numbers
   are clean: bulk insert (batched transactions + one durable commit), point reads (10 %
   misses), `GetIds(value)` lookups, range scans (~1k-row windows), range counts, updates,
   small durable transactions (fsync cost), removes.
3. **Report** — one table per scenario: ops/sec per phase, managed-heap and working-set growth
   after load, and on-disk size of the durably committed loaded state.

## Fairness caveats (also printed under the results)

- ZoneTree runs with `WriteAheadLogMode.AsyncCompressed`; its "durable" commit only saves
  metadata because ZoneTree exposes no group-commit/fsync primitive — its DurTx/s column is
  therefore overstated. The timed insert phase forces its mutable segment to merge to disk so
  reads exercise the disk path and the disk column is real.
- FASTER's ordered operations run against the in-memory secondary index (rebuilt from the log
  on open); the memory columns show the price of that design. Point reads are pure FASTER.
- The native engine and SQLite need no such assistance; their durable commits are real fsyncs.
