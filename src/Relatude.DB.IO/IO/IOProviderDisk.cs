namespace Relatude.DB.IO;
public class IODisk : IIOProvider {
    readonly bool _readOnly;
    readonly string _baseFolder;
    public string BaseFolder => _baseFolder;
    readonly bool _flushToDiskWhenFlushing;
    object _regLock = new();
    Dictionary<string, int> _openReaders = [];
    Dictionary<string, int> _openWriters = [];
    public IODisk(string baseFolder, bool flushToDiskWhenFlushing = true, bool readOnly = false) {
        _baseFolder = baseFolder;
        _readOnly = readOnly;
        if (!Directory.Exists(_baseFolder)) Directory.CreateDirectory(_baseFolder);
        _flushToDiskWhenFlushing = flushToDiskWhenFlushing;
    }
    void registerReader(string fileKey) {
        lock (_regLock) {
            if (_openReaders.ContainsKey(fileKey)) _openReaders[fileKey]++;
            else _openReaders[fileKey] = 1;
        }
    }
    void unregisterReader(string fileKey) {
        lock (_regLock) {
            if (_openReaders.ContainsKey(fileKey)) {
                _openReaders[fileKey]--;
                if (_openReaders[fileKey] <= 0) _openReaders.Remove(fileKey);
            }
        }
    }
    void registerWriter(string fileKey) {
        lock (_regLock) {
            if (_openWriters.ContainsKey(fileKey)) _openWriters[fileKey]++;
            else _openWriters[fileKey] = 1;
        }
    }
    void unregisterWriter(string fileKey) {
        lock (_regLock) {
            if (_openWriters.ContainsKey(fileKey)) {
                _openWriters[fileKey]--;
                if (_openWriters[fileKey] <= 0) _openWriters.Remove(fileKey);
            }
        }
    }
    public IReadStream OpenRead(string fileKey, long position) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        var filePath = Path.Combine(_baseFolder, fileKey);
        var stream = new StoreStreamDiscRead(filePath, position, () => {
            unregisterReader(fileKey);
        });
        registerReader(fileKey);
        return stream;
    }
    public IAppendStream OpenAppend(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        var filePath = Path.Combine(_baseFolder, fileKey);
        var stream = new StoreStreamDiscWrite(fileKey, filePath, _flushToDiskWhenFlushing, _readOnly, () => {
            unregisterWriter(fileKey);
        });
        registerWriter(fileKey);
        return stream;
    }
    public void DeleteIfItExists(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        var filePath = Path.Combine(_baseFolder, fileKey);
        if (File.Exists(filePath)) File.Delete(filePath);
    }
    public bool DoesNotExistOrIsEmpty(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        var filePath = Path.Combine(_baseFolder, fileKey);
        return !File.Exists(filePath) || new FileInfo(filePath).Length == 0;
    }
    public FileMeta[] GetFiles() {
        var files = new DirectoryInfo(_baseFolder).GetFiles().Select(FileMeta.FromFileInfo).ToArray();
        lock (_regLock) {
            foreach (var f in files) {
                if (_openReaders.ContainsKey(f.Key)) f.Readers = _openReaders[f.Key];
                if (_openWriters.ContainsKey(f.Key)) f.Writers = _openWriters[f.Key];
            }
        }
        return files;
    }
    public long GetFileSizeOrZeroIfUnknown(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        var filePath = Path.Combine(_baseFolder, fileKey);
        if (!File.Exists(filePath)) return 0;
        return new FileInfo(filePath).Length;
    }
    public void MoveFile(IODisk sourceIo, string sourceFileKey, string destFileKey, bool overwrite) {
        FileKeyUtility.ValidateFileKeyString(sourceFileKey);
        FileKeyUtility.ValidateFileKeyString(destFileKey);
        var source = Path.Combine(sourceIo._baseFolder, sourceFileKey);
        var dest = Path.Combine(_baseFolder, destFileKey);
        if (overwrite) DeleteIfItExists(destFileKey);
        if (File.Exists(destFileKey)) throw new Exception($"File {destFileKey} already exists");
        File.Move(source, dest);
    }
    public void RenameFile(string fileKey, string newFileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        FileKeyUtility.ValidateFileKeyString(newFileKey);
        var filePath = Path.Combine(_baseFolder, fileKey);
        var newFilePath = Path.Combine(_baseFolder, newFileKey);
        if (File.Exists(newFilePath)) throw new Exception($"File {newFileKey} already exists");
        File.Move(filePath, newFilePath);
    }
    public bool CanRenameFile => true;
    public void ResetLocks() {

    }
}