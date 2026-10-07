namespace Relatude.DB.DataStores.Files;

/// <summary>A file value the rewrite left pointing at its old copy, and why.</summary>
public class RewriteFileFailure {
    public Guid NodeId { get; set; }
    public string NodeType { get; set; } = string.Empty;
    public string Property { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Reason { get; set; } = string.Empty;
}
/// <summary>
/// What rewriting the files of one file store into another did: every file value pointing into the source
/// store is given a copy written by the target store - with its hash, its layout and, where it keeps one
/// copy per content, shared with an identical file - and points at that copy from then on. With the same
/// store on both sides only the values the store would now write differently are rewritten: a hash of
/// another algorithm, or a file not yet kept by its hash. The old copies are left where they are; the
/// unreferenced file cleanup removes them.
/// </summary>
public class RewriteFilesResult {
    /// <summary>The store read from; null when the values were read from wherever they were stored, as the
    /// files that belong in the target store - those of the properties uploading into it.</summary>
    public Guid? FromStoreId { get; set; }
    public Guid ToStoreId { get; set; }
    public int NodesScanned { get; set; }
    /// <summary>File values pointing into the source store - or, without one, belonging in the target store -
    /// revisions and embedded objects included.</summary>
    public int ValuesFound { get; set; }
    /// <summary>Values now pointing at a copy in the target store.</summary>
    public int ValuesRewritten { get; set; }
    /// <summary>Values already in the target store, hashed and kept the way it writes files now.</summary>
    public int ValuesUpToDate { get; set; }
    /// <summary>Distinct stored files read and written to the target store, and their bytes. A file
    /// shared by several values is copied once.</summary>
    public int FilesCopied { get; set; }
    public long BytesCopied { get; set; }
    /// <summary>Values left as they were: the file could not be read or written, or the node could not
    /// be locked. Listed up to <see cref="MaxListed"/>.</summary>
    public int FailedCount { get; set; }
    public RewriteFileFailure[] Failures { get; set; } = [];
    public bool ListTruncated { get; set; }
    public const int MaxListed = 1000;
}
