using Relatude.DB.DataStores.Indexes;
using TextIndexBenchmarks.Engines;

namespace TextIndexBenchmarks.Harness;

/// <summary>The four word-index implementations under test, and how each one is opened.</summary>
public static class Engines {
    public const string Trie = "trie";
    public const string TextIndex = "textindex";
    public const string Lucene = "lucene";
    public const string Sqlite = "sqlite";
    /// <summary>The same disk index on a deliberately tiny cache budget. Not in <see cref="All"/>:
    /// it is a second configuration of one implementation rather than a fifth implementation, but
    /// naming it in --engines shows what the budget actually buys.</summary>
    public const string TextIndexLowMem = "textindex-lowmem";
    public const long LowMemCacheBytes = 8L * 1024 * 1024;

    public static readonly string[] All = [Trie, TextIndex, Lucene, Sqlite];

    /// <summary>The reference every candidate is verified against: the implementation the other
    /// three are meant to be interchangeable with.</summary>
    public const string Reference = Trie;

    /// <summary>A fixed log id: every engine binds its data to the WAL file it belongs to, and the
    /// benchmark's reopen step has to present the same one or the index resets itself.</summary>
    public static readonly Guid WalFileId = new("6b6f7264-7465-7874-696e-646578626d6b");

    public static string DisplayName(string name) => name switch {
        Trie => "WordIndexTrie (mem)",
        TextIndex => "TextIndex (disk)",
        TextIndexLowMem => $"TextIndex ({LowMemCacheBytes / 1024 / 1024} MB cache)",
        Lucene => "Lucene",
        Sqlite => "SQLite FTS5",
        _ => name,
    };

    /// <summary>Long-form description printed under the tables.</summary>
    public static string Description(string name) => name switch {
        Trie => "in-memory char-array trie, persisted as one state file (Relatude.DB.DataStoreLocal)",
        TextIndex => "built-in disk index: LSM segments, cached term blocks and postings (Relatude.DB.TextIndex)",
        TextIndexLowMem => $"the same disk index with its cache budget set to {LowMemCacheBytes / 1024 / 1024} MB",
        Lucene => "Lucene.NET 4.8 index directory, near-real-time reader (Relatude.DB.Lucene)",
        Sqlite => "SQLite FTS5 virtual table, prefix index 2-3 (Relatude.DB.Sqlite)",
        _ => name,
    };

    public static IBenchWordIndex Create(string name, string dir, BenchOptions options, bool reopen = false) {
        Directory.CreateDirectory(dir);
        var wordOptions = new WordIndexOptions(Corpus.MinWordLength, Corpus.MaxWordLength, PrefixSearch: true, InfixSearch: options.Infix);
        return name switch {
            Trie => new TrieBenchIndex(dir, WalFileId, Corpus.MinWordLength, Corpus.MaxWordLength, prefix: true, infix: options.Infix, reopen),
            TextIndex or TextIndexLowMem => new TextEngineBenchIndex(
                new TextIndexEngine(dir, new TextIndexOptions {
                    MaxCacheBytes = name == TextIndexLowMem ? LowMemCacheBytes : options.CacheBytes,
                }),
                WalFileId, Features.Fuzzy | Features.Infix | Features.Suggest, wordOptions),
            // Lucene parses the term set through its query parser: fuzzy and leading wildcards are
            // supported, spelling suggestions are not implemented in the Relatude binding
            Lucene => new TextEngineBenchIndex(new LuceneTextIndexEngine(dir), WalFileId, Features.Fuzzy | Features.Infix, wordOptions),
            // FTS5 has no fuzzy matching and no leading wildcard, and the binding does not
            // implement suggestions - those phases are skipped rather than measured on a query
            // that quietly means something else
            Sqlite => new TextEngineBenchIndex(new SqliteIndexStore(dir), WalFileId, Features.None, wordOptions),
            _ => throw new ArgumentException($"Unknown engine '{name}'."),
        };
    }
}

public sealed class BenchOptions {
    public int N = 500_000;
    public int WordsPerDocument = 60;
    public int VocabularySize = 40_000;
    /// <summary>Documents per transaction during the index, update and remove phases.</summary>
    public int BatchSize = 2_000;
    /// <summary>Byte budget of the built-in disk index's block and postings cache.</summary>
    public long CacheBytes = 256L * 1024 * 1024;
    /// <summary>Index infix (<c>*word</c>) support. It costs index time and memory in every
    /// implementation that has it, so it is part of the measured configuration, not a query flag.</summary>
    public bool Infix = true;
    /// <summary>Persist after every batch (what the store does for persisted engines after each WAL
    /// flush) instead of once at the end of the load.</summary>
    public bool PersistEveryBatch;
    public string[] EngineNames = Engines.All;
    public string DataDir = Path.GetTempPath();
    public bool SkipVerify;
    public bool Strict;
    public bool InProcess;
    public string? ChildEngine, ChildDir;

    public static BenchOptions Parse(string[] args) {
        var o = new BenchOptions();
        foreach (var a in args) {
            var kv = a.Split('=', 2);
            switch (kv[0]) {
                case "--n": o.N = int.Parse(kv[1]); break;
                case "--words": o.WordsPerDocument = int.Parse(kv[1]); break;
                case "--vocab": o.VocabularySize = int.Parse(kv[1]); break;
                case "--batch": o.BatchSize = int.Parse(kv[1]); break;
                case "--cache": o.CacheBytes = long.Parse(kv[1]) * 1024 * 1024; break;
                case "--no-infix": o.Infix = false; break;
                case "--persist": o.PersistEveryBatch = kv[1] == "batch"; break;
                case "--engines": o.EngineNames = kv[1] == "all" ? Engines.All : kv[1].Split(','); break;
                case "--data": o.DataDir = kv[1]; break;
                case "--no-verify": o.SkipVerify = true; break;
                case "--strict": o.Strict = true; break;
                case "--in-process": o.InProcess = true; break;
                case "--child-engine": o.ChildEngine = kv[1]; break;
                case "--child-dir": o.ChildDir = kv[1]; break;
                default: throw new ArgumentException($"Unknown option '{a}'.");
            }
        }
        return o;
    }
}
