using Relatude.DB.IO;

public class FolderMeta {
    public static FolderMeta FromDirInfo(DirectoryInfo dirInfo, string relpath) {
        return new() {
            Name = dirInfo.Name,
            CreationTimeUtc = dirInfo.CreationTimeUtc,
            LastModifiedUtc = dirInfo.LastWriteTimeUtc,
            Description= FileKeyUtility.FolderTypeDescription(relpath),
        };
    }
    public FolderMeta[] SubFolders { get; set; } = [];
    public FileMeta[] Files{ get; set; } = [];
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime CreationTimeUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public string Description { get; set; } 
    public override string ToString() {
        return $"{Name} ({Description}), {Size} bytes, Created: {CreationTimeUtc:u}, Modified: {LastModifiedUtc:u}";
    }
}


