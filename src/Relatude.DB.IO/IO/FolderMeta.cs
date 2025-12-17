using Relatude.DB.IO;

public class FolderMeta {
    public static FolderMeta FromDirInfo(DirectoryInfo dirInfo) {
        return new() {
            Name = dirInfo.Name,
            CreationTimeUtc = dirInfo.CreationTimeUtc,
            LastModifiedUtc = dirInfo.LastWriteTimeUtc,
        };
    }
    public FolderMeta[] SubFolders { get; set; } = [];
    public FileMeta[] Files{ get; set; } = [];
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime CreationTimeUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public int Readers { get; set; }
    public int Writers { get; set; }
    public string Description => FileKeyUtility.FileTypeDescription(Name);
    public override string ToString() {
        return $"{Name} ({Description}), {Size} bytes, Created: {CreationTimeUtc:u}, Modified: {LastModifiedUtc:u}, Readers: {Readers}, Writers: {Writers}";
    }
}


