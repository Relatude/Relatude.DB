using Relatude.DB.IO;

public class FileMeta {
    public static FileMeta FromFileInfo(FileInfo fileInfo) {
        return new() {
            Key = fileInfo.Name,
            Size = fileInfo.Length,
            CreationTimeUtc = fileInfo.CreationTimeUtc,
            LastModifiedUtc = fileInfo.LastWriteTimeUtc,
        };
    }
    public string Key { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime CreationTimeUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public int Readers { get; set; }
    public int Writers { get; set; }
    public string Description => FileKeyUtility.FileTypeDescription(Key);
    public override string ToString() {
        return $"{Key} ({Description}), {Size} bytes, Created: {CreationTimeUtc:u}, Modified: {LastModifiedUtc:u}, Readers: {Readers}, Writers: {Writers}";
    }
}


