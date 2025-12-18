namespace Relatude.DB.IO;

public class IOProviderDisk : IIOProvider {
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
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            var filePath = Path.Combine(BaseFolder, fileKey);
            StoreStreamDiscRead? stream = null;
            stream = new StoreStreamDiscRead(filePath, position, () => {
                lock (_lock) {
                    unregisterReader(fileKey);
                    _openStreams.Remove(stream!);
                }
            });
            registerReader(fileKey);
            _openStreams.Add(stream);
            return stream;
        }
    }
    public IAppendStream OpenAppend(string fileKey) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            var filePath = Path.Combine(BaseFolder, fileKey);
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
    public void DeleteIfItExists(string fileKey) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            var filePath = Path.Combine(BaseFolder, fileKey);
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
    public long GetFileSizeOrZeroIfUnknown(string fileKey) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            var filePath = Path.Combine(BaseFolder, fileKey);
            if (!File.Exists(filePath)) return 0;
            return new FileInfo(filePath).Length;
        }
    }
    public void MoveFile(IOProviderDisk sourceIo, string sourceFileKey, string destFileKey, bool overwrite) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(sourceFileKey);
            FileKeyUtility.ValidateFileKeyString(destFileKey);
            var source = Path.Combine(sourceIo.BaseFolder, sourceFileKey);
            var dest = Path.Combine(BaseFolder, destFileKey);
            if (overwrite) DeleteIfItExists(destFileKey);
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
    public bool CanHaveSubFolders => true;
    public Task<FolderMeta[]> GetSubFolders() {
        return Task.FromResult(Array.Empty<FolderMeta>());
        //var baseFolderMeta = FolderMeta.FromDirInfo(new DirectoryInfo(BaseFolder), BaseFolder);
        //addAllSubFolders(baseFolderMeta, parentFolder);
        //return Task.FromResult(baseFolderMeta.SubFolders ?? Array.Empty<FolderMeta>());
    }
    //void addAllSubFolders(DirectoryInfo parentFolder, FolderMeta folder, string basePath) {
    //    var dirPath = Path.Combine(basePath, folder.Name);
    //    var subdirs = new DirectoryInfo(dirPath).GetDirectories().Select(f => FolderMeta.FromDirInfo(f, basePath)).ToArray();
    //    folder.SubFolders = subdirs;
    //    foreach (var subdir in subdirs) {
    //        subdir.Files = new DirectoryInfo(Path.Combine(dirPath, subdir.Name)).GetFiles().Select(FileMeta.FromFileInfo).ToArray();
    //        addAllSubFolders(subdir, dirPath);
    //    }
    //}
}