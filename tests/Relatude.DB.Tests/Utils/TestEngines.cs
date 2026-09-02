using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.IO;
using Relatude.DB.NodeServer;

namespace Relatude.Utils;

/// <summary>
/// Index engine settings for tests. A store run without the server host gets its engines from a
/// factory and its routing from the settings, and the two have to agree on the engine ids - these
/// are the ids, and <see cref="Settings"/> / <see cref="Factory"/> build both halves from the same
/// names. "Memory" (or null) stands for no engine, so a default stays <see cref="Guid.Empty"/>.
/// </summary>
public static class TestEngines {
    public static readonly Guid ValueId = new("a1a1a1a1-0000-4000-8000-000000000001");
    public static readonly Guid TextId = new("a1a1a1a1-0000-4000-8000-000000000002");
    public static readonly Guid VectorId = new("a1a1a1a1-0000-4000-8000-000000000003");
    /// <summary>A Native value engine under <see cref="ValueId"/>, for stores that construct the engine themselves.</summary>
    public static readonly IndexEngineSettings NativeValue = new() { Id = ValueId, TypeName = IndexEngineTypes.Native };
    public static readonly IndexEngineSettings NativeText = new() { Id = TextId, TypeName = IndexEngineTypes.Native };
    public static readonly IndexEngineSettings LuceneText = new() { Id = TextId, TypeName = IndexEngineTypes.Lucene };

    static bool isEngine(string? typeName) => !string.IsNullOrEmpty(typeName) && !typeName.Equals("Memory", StringComparison.OrdinalIgnoreCase);

    /// <summary>Settings whose defaults name one engine per given kind, by type name.</summary>
    public static SettingsLocal Settings(string? value = null, string? text = null, string? vector = null, int memoryMb = 64) {
        var s = new SettingsLocal();
        if (isEngine(value)) {
            s.ValueIndexes = [new() { Id = ValueId, TypeName = value, MaxMemoryUsageInMb = memoryMb }];
            s.DefaultValueIndex = ValueId;
        }
        if (isEngine(text)) {
            s.TextIndexes = [new() { Id = TextId, TypeName = text, MaxMemoryUsageInMb = memoryMb }];
            s.DefaultTextIndex = TextId;
        }
        if (isEngine(vector)) {
            s.VectorIndexes = [new() { Id = VectorId, TypeName = vector, MaxMemoryUsageInMb = memoryMb }];
            s.DefaultVectorIndex = VectorId;
        }
        return s;
    }

    /// <summary>
    /// The engine factory the server host would build for these settings, with the engines below
    /// <paramref name="dir"/>/indexes - the very code path production runs, dual-role SQLite included.
    /// Null when every default is the memory index.
    /// </summary>
    public static Func<IndexEngines>? Factory(string dir, SettingsLocal settings, bool hasAiProvider = false) {
        return NodeStoreContainer.CreateIndexEngineFactory(settings, Path.Combine(dir, FileKeyUtility.IndexStoreFolderKey), hasAiProvider, null, []);
    }
}
