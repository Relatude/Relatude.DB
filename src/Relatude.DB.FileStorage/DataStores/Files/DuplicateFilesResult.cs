namespace Relatude.DB.DataStores.Files;

/// <summary>One content stored more than once in a file store: the same hash and size under several
/// file ids.</summary>
public class DuplicateFileGroup {
    public Guid StorageId { get; set; }
    public string Hash { get; set; } = string.Empty;
    public long Size { get; set; }
    /// <summary>How many times the bytes are stored: the distinct file ids carrying them.</summary>
    public int Copies { get; set; }
    /// <summary>How many file values point at one of the copies.</summary>
    public int Values { get; set; }
    /// <summary>What all copies but one take up.</summary>
    public long DuplicateBytes { get; set; }
    /// <summary>A few of the file values, as "Type.Property - file name".</summary>
    public string[] Examples { get; set; } = [];
    public const int MaxExamples = 3;
}
/// <summary>
/// How much of the file stores holds the same content more than once, read from the hash and size
/// every file value carries - no file is read. A stored file is one file id in one store; content is
/// compared within a store only, as that is where SameHashSameFile can share it.
/// </summary>
public class DuplicateFilesResult {
    public int NodesScanned { get; set; }
    public int ValuesChecked { get; set; }
    /// <summary>Distinct stored files the values point at.</summary>
    public int StoredFiles { get; set; }
    public long StoredBytes { get; set; }
    /// <summary>Distinct contents among the stored files.</summary>
    public int DistinctContents { get; set; }
    /// <summary>Stored files whose content another stored file of the same store already holds.</summary>
    public int DuplicateFiles { get; set; }
    /// <summary>What the duplicate files take up: what keeping one copy per content would save.</summary>
    public long DuplicateBytes { get; set; }
    /// <summary>Values pointing at a stored file another value points at too (SameHashSameFile, or a
    /// value copied to another node), and what storing each of them separately would have taken.</summary>
    public int SharedValues { get; set; }
    public long SharedBytes { get; set; }
    /// <summary>Values with no recorded hash, which cannot be compared and are left out.</summary>
    public int ValuesWithoutHash { get; set; }
    /// <summary>The groups wasting the most bytes first, capped at <see cref="MaxListed"/>.</summary>
    public DuplicateFileGroup[] Groups { get; set; } = [];
    public bool ListTruncated { get; set; }
    public const int MaxListed = 100;
}
