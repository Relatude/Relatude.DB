namespace Relatude.DB.DataStores.Files;

/// <summary>One file value whose file could not be found in the file store it points at.</summary>
public class MissingFileInfo {
    public Guid NodeId { get; set; }
    public string NodeType { get; set; } = string.Empty;
    /// <summary>The property the value sits on, embedded properties written as a path ("Photos.Image").</summary>
    public string Property { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    /// <summary>The size recorded on the file value, not the size on disk (there is no file).</summary>
    public long Size { get; set; }
    public Guid FileId { get; set; }
    public Guid StorageId { get; set; }
    /// <summary>Why the file is reported: not found in the store, a size mismatch, or an unreachable store.</summary>
    public string Reason { get; set; } = string.Empty;
}
public class MissingFilesResult {
    public int NodesScanned { get; set; }
    public int FilesChecked { get; set; }
    public int MissingCount { get; set; }
    /// <summary>Total size the missing files were recorded as having.</summary>
    public long MissingBytes { get; set; }
    /// <summary>The missing files, capped at <see cref="MaxListed"/>; see <see cref="ListTruncated"/>.</summary>
    public MissingFileInfo[] Missing { get; set; } = [];
    public bool ListTruncated { get; set; }
    public const int MaxListed = 1000;
}
