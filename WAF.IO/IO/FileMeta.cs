
using System.Xml.Linq;

namespace WAF.IO;
public class FileMetaLight(string key, long size, DateTime creationTimeUtc, DateTime lastModifiedUtc) {

    public static FileMetaLight FromFileInfo(FileInfo f) {
        return new(f.Name, f.Length, f.CreationTimeUtc, f.LastWriteTimeUtc);
    }

    public string Key { get; } = key;
    public long Size { get; } = size;
    public DateTime CreationTimeUtc { get; } = creationTimeUtc;
    public DateTime LastModifiedUtc { get; } = lastModifiedUtc;
    public FileMeta ToFileMeta() {
        return new() {
            Key = Key,
            Size = Size,
            CreationTimeUtc = CreationTimeUtc,
            LastModifiedUtc = LastModifiedUtc,
        };
    }
}
public class FileMeta {
    public string Key { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime CreationTimeUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public int Readers { get; set; }
    public int Writers { get; set; }
    public string Description => FileKeyUtility.FileTypeDescription(Key);
}


