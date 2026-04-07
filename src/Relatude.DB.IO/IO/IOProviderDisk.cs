using System.Runtime;

namespace Relatude.DB.IO;

public class IOProviderDisk : IIOProviderWithFolders {
    readonly bool _readOnly;
    readonly object _lock = new();
    readonly Dictionary<string, int> _openReaders = [];
    readonly Dictionary<string, int> _openWriters = [];
    readonly List<IStream> _openStreams = [];
    public IOProviderDisk(string baseFolder, bool readOnly = false) {
        BaseFolder = baseFolder;
        _readOnly = readOnly;
        if (!Directory.Exists(BaseFolder)) Directory.CreateDirectory(BaseFolder);
    }
    public string BaseFolder { get; }
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
    public IReadStream OpenRead(string fileKey, long position) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        return openRead(Path.Combine(BaseFolder, fileKey), position);
    }
    public IReadStream OpenRead(string[] path, long position) {
        FileKeyUtility.ValidateFileKeyPath(path);
        return openRead(Path.Combine([BaseFolder, .. path]), position);
    }
    public bool Exists(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        var filePath = Path.Combine(BaseFolder, fileKey);
        return File.Exists(filePath);
    }
    public bool Exists(string[] path) {
        FileKeyUtility.ValidateFileKeyPath(path);
        var filePath = Path.Combine([BaseFolder, .. path]);
        return File.Exists(filePath);
    }
    IReadStream openRead(string filePath, long position) {
        lock (_lock) {
            IReadStream? stream = null;
            stream = new StoreStreamDiscRead(filePath, position, () => {
                lock (_lock) {
                    unregisterReader(filePath);
                    _openStreams.Remove(stream!);
                }
            });
            stream = new StoreStreamBufferedRead(stream, 1024 * 1024); // turned out that buffering helps a lot in any case
            registerReader(filePath);
            _openStreams.Add(stream);
            return stream;
        }
    }
    public IAppendStream OpenAppend(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        var filePath = Path.Combine(BaseFolder, fileKey);
        return openAppend(filePath, fileKey);
    }
    public IAppendStream OpenAppend(string[] path) {
        FileKeyUtility.ValidateFileKeyPath(path);
        var filePath = Path.Combine([BaseFolder, .. path]);
        var fileKey = Path.Combine(path);
        return openAppend(filePath, fileKey);
    }
    IAppendStream openAppend(string filePath, string fileKey) {
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
    public void DeleteFileIfItExists(string fileKey) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            var filePath = Path.Combine(BaseFolder, fileKey);
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }
    public void DeleteFileIfItExists(string[] path) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyPath(path);
            var filePath = Path.Combine([BaseFolder, .. path]);
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }
    public bool DoesNotExistOrIsEmpty(string fileKey) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            var filePath = Path.Combine(BaseFolder, fileKey);
            return !File.Exists(filePath) || new FileInfo(filePath).Length == 0;
        }
    }
    public long GetFileSizeOrZeroIfUnknown(string fileKey) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            var filePath = Path.Combine(BaseFolder, fileKey);
            if (!File.Exists(filePath)) return 0;
            return new FileInfo(filePath).Length;
        }
    }
    public long GetFileSizeOrZeroIfUnknown(string[] path) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyPath(path);
            var filePath = Path.Combine([BaseFolder, .. path]);
            if (!File.Exists(filePath)) return 0;
            return new FileInfo(filePath).Length;
        }
    }
    public FileMeta[] GetFiles() {
        lock (_lock) {
            var files = new DirectoryInfo(BaseFolder).GetFiles().Select(FileMeta.FromFileInfo).ToArray();
            lock (_lock) {
                foreach (var f in files) {
                    if (_openReaders.ContainsKey(f.Key)) f.Readers = _openReaders[f.Key];
                    if (_openWriters.ContainsKey(f.Key)) f.Writers = _openWriters[f.Key];
                }
            }
            return files;
        }
    }
    public void MoveFile(IOProviderDisk sourceIo, string sourceFileKey, string destFileKey, bool overwrite) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(sourceFileKey);
            FileKeyUtility.ValidateFileKeyString(destFileKey);
            var source = Path.Combine(sourceIo.BaseFolder, sourceFileKey);
            var dest = Path.Combine(BaseFolder, destFileKey);
            if (overwrite) DeleteFileIfItExists(destFileKey);
            if (File.Exists(destFileKey)) throw new Exception($"File {destFileKey} already exists");
            File.Move(source, dest);
        }
    }
    public void RenameFile(string fileKey, string newFileKey) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            FileKeyUtility.ValidateFileKeyString(newFileKey);
            var filePath = Path.Combine(BaseFolder, fileKey);
            var newFilePath = Path.Combine(BaseFolder, newFileKey);
            if (File.Exists(newFilePath)) throw new Exception($"File {newFileKey} already exists");
            File.Move(filePath, newFilePath);
        }
    }
    public bool CanRenameFile => true;

    public void CloseAllOpenStreams() {
        lock (_lock) {
            foreach (var stream in _openStreams.ToArray()) {
                stream.Dispose();
            }
            if (_openStreams.Count != 0) throw new Exception("Not all streams could be closed. ");
        }
    }
    public Task<FolderMeta[]> GetFoldersAsync(string[] path, bool recursive, bool withFiles) {
        FileKeyUtility.ValidateFileKeyPath(path);
        var baseFolderMeta = FolderMeta.FromDirInfo(new DirectoryInfo(BaseFolder), BaseFolder);
        var dirInfo = new DirectoryInfo(BaseFolder);
        addAllSubFolders(dirInfo, baseFolderMeta, Path.Combine(path), recursive, withFiles);
        return Task.FromResult(baseFolderMeta.SubFolders);
    }
    void addAllSubFolders(DirectoryInfo dirInfo, FolderMeta folder, string relativeParentPath, bool recursive, bool withFiles) {
        if (withFiles) folder.Files = [.. dirInfo.GetFiles().Select(FileMeta.FromFileInfo)];
        folder.SubFolders = [.. dirInfo.GetDirectories().Select(d => FolderMeta.FromDirInfo(d, Path.Combine(relativeParentPath, d.Name)))];
        if (recursive) {
            foreach (var subFolder in folder.SubFolders) {
                var subDirInfo = new DirectoryInfo(Path.Combine(dirInfo.FullName, subFolder.Name));
                addAllSubFolders(subDirInfo, subFolder, Path.Combine(relativeParentPath, subFolder.Name), recursive, withFiles);
            }
        }
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
    void deleteFoldersAndFiles(string fileOrFolderKey) {
        FileKeyUtility.ValidateFileKeyString(fileOrFolderKey);
        if (Directory.Exists(fileOrFolderKey)) {
            var dirInfo = new DirectoryInfo(fileOrFolderKey);
            foreach (var subDir in dirInfo.GetDirectories()) {
                deleteFoldersAndFiles(subDir.FullName);
            }
            foreach (var file in dirInfo.GetFiles()) {
                file.Delete();
            }
            Directory.Delete(fileOrFolderKey);
        }
    }
    public void EnsureFolder(string[] path) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyPath(path);
            var folderPath = Path.Combine([BaseFolder, .. path]);
            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
        }
    }

}