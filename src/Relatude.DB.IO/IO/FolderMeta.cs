namespace Relatude.DB.IO;

public class FolderMeta {
    public static FolderMeta FromDirInfo(DirectoryInfo dirInfo, string relpath) => FromDirInfo(dirInfo, relpath, describe: true);
    /// <param name="describe">false for a folder that is not database storage: its "data" or "files"
    /// folders are whatever the owner put there, not the database's, so they get no description
    /// and are not marked as primary data</param>
    public static FolderMeta FromDirInfo(DirectoryInfo dirInfo, string relpath, bool describe) {
        var folder = new FolderMeta {
            Name = dirInfo.Name,
            CreationTimeUtc = dirInfo.CreationTimeUtc,
            LastModifiedUtc = dirInfo.LastWriteTimeUtc,
            HasFiles = dirInfo.EnumerateFiles().Any(),
            HasSubFolders = dirInfo.EnumerateDirectories().Any(),
        };
        if (describe) folder.Describe(relpath);
        return folder;
    }
    /// <summary>Fills in what the folder holds from its path below the storage root. Providers
    /// that build folders from virtual paths (memory, blob storage) call this themselves so every
    /// listing describes and marks the well known folders the same way.</summary>
    public FolderMeta Describe(string relpath) {
        Description = FileKeyUtility.FolderTypeDescription(relpath);
        IsPrimaryData = FileKeyUtility.IsPrimaryDataFolder(relpath);
        return this;
    }
    public FolderMeta[] SubFolders { get; set; } = [];
    public FileMeta[] Files{ get; set; } = [];
    public bool HasFiles { get; set; }
    public bool HasSubFolders { get; set; }
    public bool IsEmpty => !HasFiles && !HasSubFolders;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime CreationTimeUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastModifiedUtc { get; set; } = DateTime.UtcNow;
    public string? Description { get; set; }
    /// <summary>This folder is, or is below, one of <see cref="FileKeyUtility.PrimaryDataFolderNames"/>:
    /// what it holds exists nowhere else and cannot be rebuilt.</summary>
    public bool IsPrimaryData { get; set; }
    public override string ToString() {
        return $"{Name} ({Description}), {Size} bytes, Created: {CreationTimeUtc:u}, Modified: {LastModifiedUtc:u}";
    }
}


