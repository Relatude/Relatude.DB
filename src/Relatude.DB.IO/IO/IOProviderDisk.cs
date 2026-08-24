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
    public IOProviderDisk(string baseFolder, bool readOnly = false) {
        _providers.Add(this);
        BaseFolder = baseFolder;
        _readOnly = readOnly;
        _dirExists = Directory.Exists(BaseFolder);
    }
    void ensureFolder() {
        if (_dirExists) return;
        if (!Directory.Exists(BaseFolder)) Directory.CreateDirectory(BaseFolder);
        _dirExists = true;
    }
    public string BaseFolder { get; }
    string filePathOf(string[] path) {
        FileKeyUtility.ValidateFileKeyPath(path);
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
            // root files plus the well known system folders (data/state/bkup/log); other folders
            // (the indexes folder, the multi file store folder) own their content and are not listed
            var files = new List<FileMeta>(new DirectoryInfo(BaseFolder).GetFiles().Select(FileMeta.FromFileInfo));
            foreach (var folder in FileKeyUtility.SystemFolderNames) {
                var dir = new DirectoryInfo(Path.Combine(BaseFolder, folder));
                if (!dir.Exists) continue;
                files.AddRange(dir.GetFiles().Select(f => FileMeta.FromFileInfo(f, folder + "/" + f.Name)));
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
            if (File.Exists(newFilePath)) throw new Exception($"File {newPath.AsKeyString()} already exists");
            ensureParentFolder(newFilePath);
            File.Move(filePath, newFilePath);
        }
    }
    static void ensureParentFolder(string filePath) {
        var dir = Path.GetDirectoryName(filePath);
        if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }
    public bool CanRenameFile => true;

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

    public Task<FolderMeta> GetFolderAsync(string[] path, bool recursive, bool withFiles) {
        lock (_lock) {
            ensureFolder();
            FileKeyUtility.ValidateFileKeyPath(path);
            var relativePath = string.Join('/', path);
            var dirInfo = new DirectoryInfo(Path.Combine([BaseFolder, .. path]));
            if (!dirInfo.Exists) return Task.FromResult(new FolderMeta { Name = path.Length > 0 ? path[^1] : "" });
            var folderMeta = FolderMeta.FromDirInfo(dirInfo, relativePath);
            addAllSubFolders(dirInfo, folderMeta, relativePath, recursive, withFiles);
            return Task.FromResult(folderMeta);
        }
    }
    void addAllSubFolders(DirectoryInfo dirInfo, FolderMeta folder, string relativeParentPath, bool recursive, bool withFiles) {
        if (withFiles) folder.Files = [.. dirInfo.GetFiles().Select(f => fileMetaWithLockCounts(f, relativeKey(relativeParentPath, f.Name)))];
        folder.SubFolders = [.. dirInfo.GetDirectories().Select(d => FolderMeta.FromDirInfo(d, relativeKey(relativeParentPath, d.Name)))];
        if (recursive) {
            foreach (var subFolder in folder.SubFolders) {
                var subDirInfo = new DirectoryInfo(Path.Combine(dirInfo.FullName, subFolder.Name));
                addAllSubFolders(subDirInfo, subFolder, relativeKey(relativeParentPath, subFolder.Name), recursive, withFiles);
            }
        }
    }
    static string relativeKey(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;
    FileMeta fileMetaWithLockCounts(FileInfo fileInfo, string key) {
        var meta = FileMeta.FromFileInfo(fileInfo, key);
        if (_openReaders.TryGetValue(key, out var readers)) meta.Readers = readers;
        if (_openWriters.TryGetValue(key, out var writers)) meta.Writers = writers;
        return meta;
    }
    public void DeleteFolderIfItExists(string[] path) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyPath(path);
            var folderPath = Path.Combine([BaseFolder, .. path]);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            //if (Directory.Exists(folderPath)) Directory.Delete(folderPath, true);
            deleteFoldersAndFiles(folderPath);
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
            FileKeyUtility.ValidateFileKeyPath(path);
            var folderPath = Path.Combine([BaseFolder, .. path]);
            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
        }
    }
    public bool TryGetLocalFilePath(string[] path, [MaybeNullWhen(false)] out string localFilePath) {
        FileKeyUtility.ValidateFileKeyPath(path);
        var filePath = Path.Combine([BaseFolder, .. path]);
        if (File.Exists(filePath)) {
            localFilePath = filePath;
            return true;
        }
        localFilePath = null;
        return false;
    }
    public bool TryGetLocalFolderPath(string[] path, [MaybeNullWhen(false)] out string localFolderPath) {
        FileKeyUtility.ValidateFileKeyPath(path);
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
