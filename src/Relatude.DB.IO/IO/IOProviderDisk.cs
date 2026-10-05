using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.IO;

public class IOProviderDisk : IIOProvider {

    static List<IOProviderDisk> _providers = new List<IOProviderDisk>();
    public static string[] GetAllOpenStreams() {
        return _providers.SelectMany(p => p.GetOpenStreams()).ToArray();
    }
    public string[] GetOpenStreams() {
        lock (_lock) {
            return _openStreams.Select(s => s.FileKey).ToArray();
        }
    }

    readonly bool _readOnly;
    readonly object _lock = new();
    // internally files are tracked by the joined ('/'-separated) form of their key
    readonly Dictionary<string, int> _openReaders = [];
    readonly Dictionary<string, int> _openWriters = [];
    readonly List<IStream> _openStreams = [];
    bool _dirExists;
    /// <param name="plainFolder">The folder is an ordinary folder, not database storage (the admin UI's
    /// view of the website project folder): any legal file system name is a valid key segment, and
    /// listings carry none of the database folder descriptions or primary data markings.</param>
    public IOProviderDisk(string baseFolder, bool readOnly = false, bool plainFolder = false) {
        _providers.Add(this);
        BaseFolder = baseFolder;
        _readOnly = readOnly;
        PlainFolder = plainFolder;
        _dirExists = Directory.Exists(BaseFolder);
    }
    void ensureFolder() {
        if (_dirExists) return;
        if (!Directory.Exists(BaseFolder)) Directory.CreateDirectory(BaseFolder);
        _dirExists = true;
    }
    public string BaseFolder { get; }
    /// <summary>See the constructor: an ordinary folder rather than database storage.</summary>
    public bool PlainFolder { get; }
    /// <summary>Whether the segment is acceptable as a key segment of this provider.</summary>
    public bool IsValidKeySegment(string segment) => PlainFolder ? FileKeyUtility.IsPlainFileNameValid(segment) : FileKeyUtility.IsFileKeyValid(segment);
    void validate(string[] path) {
        if (PlainFolder) FileKeyUtility.ValidatePlainPath(path);
        else FileKeyUtility.ValidateFileKeyPath(path);
    }
    string filePathOf(string[] path) {
        validate(path);
        return Path.Combine([BaseFolder, .. path]);
    }
    void registerReader(string fileKey) {
        if (_openReaders.ContainsKey(fileKey)) _openReaders[fileKey]++;
        else _openReaders[fileKey] = 1;
    }
    void unregisterReader(string fileKey) {
        if (_openReaders.ContainsKey(fileKey)) {
            _openReaders[fileKey]--;
            if (_openReaders[fileKey] <= 0) _openReaders.Remove(fileKey);
        }
    }
    void registerWriter(string fileKey) {
        if (_openWriters.ContainsKey(fileKey)) _openWriters[fileKey]++;
        else _openWriters[fileKey] = 1;
    }
    void unregisterWriter(string fileKey) {
        lock (_lock) {
            if (_openWriters.ContainsKey(fileKey)) {
                _openWriters[fileKey]--;
                if (_openWriters[fileKey] <= 0) _openWriters.Remove(fileKey);
            }
        }
    }
    public IReadStream OpenRead(string[] path, long position) {
        var filePath = filePathOf(path);
        var fileKey = path.AsKeyString();
        lock (_lock) {
            IReadStream? stream = null;
            stream = new StoreStreamDiscRead(filePath, position, () => {
                lock (_lock) {
                    unregisterReader(fileKey);
                    _openStreams.Remove(stream!);
                }
            });
            stream = new StoreStreamBufferedRead(stream, 1024 * 1024); // turned out that buffering helps a lot in any case
            registerReader(fileKey);
            _openStreams.Add(stream);
            return stream;
        }
    }
    public bool Exists(string[] path) {
        return File.Exists(filePathOf(path));
    }
    public IAppendStream OpenAppend(string[] path) {
        var filePath = filePathOf(path);
        var fileKey = path.AsKeyString();
        lock (_lock) {
            StoreStreamDiscWrite? stream = null;
            stream = new StoreStreamDiscWrite(fileKey, filePath, _readOnly, () => {
                lock (_lock) {
                    unregisterWriter(fileKey);
                    _openStreams.Remove(stream!);
                }
            });
            registerWriter(fileKey);
            _openStreams.Add(stream);
            return stream;
        }
    }
    public void DeleteFileIfItExists(string[] path) {
        lock (_lock) {
            var filePath = filePathOf(path);
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }
    public bool DoesNotExistOrIsEmpty(string[] path) {
        lock (_lock) {
            var filePath = filePathOf(path);
            return !File.Exists(filePath) || new FileInfo(filePath).Length == 0;
        }
    }
    public long GetFileSizeOrZeroIfUnknown(string[] path) {
        lock (_lock) {
            var filePath = filePathOf(path);
            if (!File.Exists(filePath)) return 0;
            return new FileInfo(filePath).Length;
        }
    }
    public FileMeta[] GetFiles() {
        lock (_lock) {
            if (!Directory.Exists(BaseFolder)) return [];
            // root files plus the well known system folders (data/state/backup/log); other folders
            // (the indexes folder, the multi file store folder) own their content and are not listed
            var files = new List<FileMeta>(new DirectoryInfo(BaseFolder).GetFiles().Select(FileMeta.FromFileInfo));
            foreach (var folder in FileKeyUtility.SystemFolderNames) {
                var dir = new DirectoryInfo(Path.Combine(BaseFolder, folder));
                if (!dir.Exists) continue;
                files.AddRange(dir.GetFiles().Select(f => FileMeta.FromFileInfo(f, folder + "/" + f.Name)));
                // every log keeps its files in a folder of its own below the log folder (log/{key}/)
                if (folder != FileKeyUtility.LogFolderName) continue;
                foreach (var sub in foldersOf(dir)) {
                    files.AddRange(filesOf(sub).Select(f => FileMeta.FromFileInfo(f, folder + "/" + sub.Name + "/" + f.Name)));
                }
            }
            foreach (var f in files) {
                if (_openReaders.ContainsKey(f.Key)) f.Readers = _openReaders[f.Key];
                if (_openWriters.ContainsKey(f.Key)) f.Writers = _openWriters[f.Key];
            }
            return files.ToArray();
        }
    }
    public void MoveFile(IOProviderDisk sourceIo, string[] sourcePath, string[] destPath, bool overwrite) {
        lock (_lock) {
            ensureFolder();
            FileKeyUtility.ValidateFileKeyPath(sourcePath);
            var source = Path.Combine([sourceIo.BaseFolder, .. sourcePath]);
            var dest = filePathOf(destPath);
            if (overwrite) DeleteFileIfItExists(destPath);
            if (File.Exists(dest)) throw new Exception($"File {destPath.AsKeyString()} already exists");
            ensureParentFolder(dest);
            File.Move(source, dest);
        }
    }
    public void RenameFile(string[] path, string[] newPath) {
        lock (_lock) {
            var filePath = filePathOf(path);
            var newFilePath = filePathOf(newPath);
            // a change of case only is a real rename on a case insensitive file system, not a collision
            if (File.Exists(newFilePath) && !string.Equals(filePath, newFilePath, StringComparison.OrdinalIgnoreCase)) throw new Exception($"File {newPath.AsKeyString()} already exists");
            ensureParentFolder(newFilePath);
            File.Move(filePath, newFilePath);
        }
    }
    static void ensureParentFolder(string filePath) {
        var dir = Path.GetDirectoryName(filePath);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }
    public bool CanRenameFile => true;
    public bool CanRenameFolder => !_readOnly;
    public bool SupportsEmptyFolders => true;
    public void RenameFolder(string[] path, string[] newPath) {
        lock (_lock) {
            if (_readOnly) throw new Exception("The IO provider is read only. ");
            if (path.Length == 0 || newPath.Length == 0) throw new ArgumentException("The storage root cannot be renamed. ");
            validate(path);
            validate(newPath);
            var folderPath = Path.Combine([BaseFolder, .. path]);
            var newFolderPath = Path.Combine([BaseFolder, .. newPath]);
            if (!Directory.Exists(folderPath)) throw new DirectoryNotFoundException("Folder not found: " + path.AsKeyString());
            var caseChangeOnly = string.Equals(folderPath, newFolderPath, StringComparison.OrdinalIgnoreCase);
            if (!caseChangeOnly && (Directory.Exists(newFolderPath) || File.Exists(newFolderPath))) throw new Exception($"{newPath.AsKeyString()} already exists");
            // a file with an open stream would be moved out from under it (or block the move)
            var prefix = path.AsKeyString() + FileKeyExtensions.KeyDelimiter;
            var open = _openStreams.Select(s => s.FileKey).FirstOrDefault(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (open != null) throw new Exception($"File {open} is in use, the folder cannot be renamed. ");
            ensureParentFolder(newFolderPath);
            Directory.Move(folderPath, newFolderPath);
        }
    }

    public bool CanTruncate => !_readOnly;
    public void TruncateFile(string[] path, long newLength) {
        lock (_lock) {
            if (_readOnly) throw new Exception("The IO provider is read only. ");
            var filePath = filePathOf(path);
            var fileKey = path.AsKeyString();
            if (!File.Exists(filePath)) throw new FileNotFoundException("File not found: " + fileKey);
            if (_openReaders.ContainsKey(fileKey) || _openWriters.ContainsKey(fileKey))
                throw new Exception($"File {fileKey} has open streams and cannot be truncated. ");
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (newLength < 0 || newLength > fs.Length)
                throw new ArgumentOutOfRangeException(nameof(newLength), $"New length {newLength} is outside the file (0-{fs.Length}). ");
            fs.SetLength(newLength);
            fs.Flush(true);
        }
    }

    public void CloseAllOpenStreams() {
        lock (_lock) {
            foreach (var stream in _openStreams.ToArray()) {
                stream.Dispose();
            }
            if (_openStreams.Count != 0) throw new Exception("Not all streams could be closed. ");
        }
    }

    /// <summary>
    /// The folder as it is on disk, with the open stream counts of its files.
    /// <para>The provider lock is held only while those counts are copied, not while the folders are
    /// walked. A walk is a trip to the disk per folder - several on a network share such as the Azure
    /// Files mount behind an App Service's home folder - so listing a large tree takes seconds, and
    /// with the lock held for all of it every other file of the provider waited: the database opening
    /// a file, a download, a second listing. The counts are a snapshot taken as the listing starts,
    /// which is all a listing of a folder that can change under it ever was.</para>
    /// </summary>
    public Task<FolderMeta> GetFolderAsync(string[] path, bool recursive, bool withFiles) {
        Dictionary<string, int> readers, writers;
        lock (_lock) {
            ensureFolder();
            validate(path);
            readers = new(_openReaders);
            writers = new(_openWriters);
        }
        var relativePath = string.Join('/', path);
        var dirInfo = new DirectoryInfo(Path.Combine([BaseFolder, .. path]));
        if (!dirInfo.Exists) {
            var missing = new FolderMeta { Name = path.Length > 0 ? path[^1] : "" };
            return Task.FromResult(PlainFolder ? missing : missing.Describe(relativePath));
        }
        var folderMeta = FolderMeta.FromDirInfo(dirInfo, relativePath, describe: !PlainFolder);
        addAllSubFolders(dirInfo, folderMeta, relativePath, recursive, withFiles, readers, writers);
        return Task.FromResult(folderMeta);
    }
    void addAllSubFolders(DirectoryInfo dirInfo, FolderMeta folder, string relativeParentPath, bool recursive, bool withFiles,
        Dictionary<string, int> readers, Dictionary<string, int> writers) {
        if (withFiles) folder.Files = [.. filesOf(dirInfo).Select(f => fileMetaWithLockCounts(f, relativeKey(relativeParentPath, f.Name), readers, writers))];
        var subDirs = foldersOf(dirInfo);
        // A walk of the whole tree lists every folder below anyway, so asking each one first whether
        // it holds files and folders - two more trips to the disk a folder, on top of the two the walk
        // makes - is only worth it where the walk stops, and the answer is read off what it found.
        folder.SubFolders = [.. subDirs.Select(d => FolderMeta.FromDirInfo(d, relativeKey(relativeParentPath, d.Name), describe: !PlainFolder, probe: !recursive))];
        if (recursive) {
            for (var i = 0; i < subDirs.Length; i++) {
                var subFolder = folder.SubFolders[i];
                addAllSubFolders(subDirs[i], subFolder, relativeKey(relativeParentPath, subFolder.Name), recursive, withFiles, readers, writers);
                subFolder.HasSubFolders = subFolder.SubFolders.Length > 0;
                subFolder.HasFiles = withFiles ? subFolder.Files.Length > 0 : hasFiles(subDirs[i]);
            }
        }
    }
    // A folder that goes away while the tree is walked (a rebuilt index replacing its segment
    // folder, say) is a folder that is no longer there, not a listing that failed.
    static FileInfo[] filesOf(DirectoryInfo dir) {
        try { return dir.GetFiles(); } catch (DirectoryNotFoundException) { return []; }
    }
    static DirectoryInfo[] foldersOf(DirectoryInfo dir) {
        try { return dir.GetDirectories(); } catch (DirectoryNotFoundException) { return []; }
    }
    static bool hasFiles(DirectoryInfo dir) {
        try { return dir.EnumerateFiles().Any(); } catch (DirectoryNotFoundException) { return false; }
    }
    static string relativeKey(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;
    static FileMeta fileMetaWithLockCounts(FileInfo fileInfo, string key, Dictionary<string, int> openReaders, Dictionary<string, int> openWriters) {
        var meta = FileMeta.FromFileInfo(fileInfo, key);
        if (openReaders.TryGetValue(key, out var readers)) meta.Readers = readers;
        if (openWriters.TryGetValue(key, out var writers)) meta.Writers = writers;
        return meta;
    }
    public void DeleteFolderIfItExists(string[] path) {
        lock (_lock) {
            validate(path);
            var folderPath = Path.Combine([BaseFolder, .. path]);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            //if (Directory.Exists(folderPath)) Directory.Delete(folderPath, true);
            deleteFoldersAndFiles(folderPath);
        }
    }
    public bool DeleteFolderIfEmpty(string[] path) {
        if (path.Length == 0) return false;
        // under the lock that OpenAppend and RenameFile create folders and files under, so a file is
        // either in the folder already (and it stays) or is written after it is gone (and recreates it)
        lock (_lock) {
            validate(path);
            var folderPath = Path.Combine([BaseFolder, .. path]);
            try {
                Directory.Delete(folderPath, false);
            } catch (DirectoryNotFoundException) {
            } catch (IOException) {
                return false; // not empty, or a file in it is still held open after being deleted
            }
            return true;
        }
    }
    void deleteFoldersAndFiles(string fullFolderPath) {
        if (Directory.Exists(fullFolderPath)) {
            var dirInfo = new DirectoryInfo(fullFolderPath);
            foreach (var subDir in dirInfo.GetDirectories()) {
                deleteFoldersAndFiles(subDir.FullName);
            }
            foreach (var file in dirInfo.GetFiles()) {
                file.Delete();
            }
            Directory.Delete(fullFolderPath);
        }
    }
    public void EnsureFolder(string[] path) {
        lock (_lock) {
            validate(path);
            var folderPath = Path.Combine([BaseFolder, .. path]);
            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
        }
    }
    public bool TryGetLocalFilePath(string[] path, [MaybeNullWhen(false)] out string localFilePath) {
        validate(path);
        var filePath = Path.Combine([BaseFolder, .. path]);
        if (File.Exists(filePath)) {
            localFilePath = filePath;
            return true;
        }
        localFilePath = null;
        return false;
    }
    public bool TryGetLocalFolderPath(string[] path, [MaybeNullWhen(false)] out string localFolderPath) {
        validate(path);
        var folderPath = Path.Combine([BaseFolder, .. path]);
        localFolderPath = folderPath;
        return true;
    }
    public bool TryMoveIfSameDrive(string fromLocalFilePath, string[] destination) {
        var destinationPath = Path.Combine([BaseFolder, .. destination]);
        var isSameDrive = string.Equals(Path.GetPathRoot(fromLocalFilePath), Path.GetPathRoot(destinationPath), StringComparison.OrdinalIgnoreCase);
        if (isSameDrive) {
            try {
                // ensure destination directory exists:
                var destinationDir = Path.GetDirectoryName(destinationPath);
                if (destinationDir == null) return false;
                if (!Directory.Exists(destinationDir)) Directory.CreateDirectory(destinationDir);
                File.Move(fromLocalFilePath, destinationPath);
                return true;
            } catch {
                return false;
            }
        }
        return false;
    }
}
