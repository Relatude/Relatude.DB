namespace WAF.IO;
public class IODisk : IIOProvider {
    readonly bool _readOnly;
    readonly string _baseFolder;
    readonly DirectoryManager _dirManager;
    public string BaseFolder => _baseFolder;
    public IODisk(string baseFolder, bool readOnly = false) {
        _baseFolder = baseFolder;
        _readOnly = readOnly;
        if (!Directory.Exists(_baseFolder)) Directory.CreateDirectory(_baseFolder);
        var refresh = () => new DirectoryInfo(_baseFolder).GetFiles().Select(FileMetaLight.FromFileInfo).ToArray();
        _dirManager = new(refresh);
    }
    public IReadStream OpenRead(string fileKey, long position) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_dirManager) {
            _dirManager.RegisterReader(fileKey);
            var filePath = Path.Combine(_baseFolder, fileKey);
            return new StoreStreamDiscRead(filePath, position, () => {
                lock (_dirManager) _dirManager.DeRegisterReader(fileKey);
            });
        }
    }
    public IAppendStream OpenAppend(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_dirManager) {
            _dirManager.ValidateWriter(fileKey);
            var filePath = Path.Combine(_baseFolder, fileKey);
            var stream = new StoreStreamDiscWrite(fileKey, filePath, _readOnly, () => {
                lock (_dirManager) _dirManager.DeRegisterWriter(fileKey);
            });
            _dirManager.RegisterWriter(fileKey);
            return stream;
        }
    }
    public void DeleteIfItExists(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_dirManager) {
            _dirManager.ValidateCanDeleteIfItExists(fileKey);
            var filePath = Path.Combine(_baseFolder, fileKey);
            try {
                if (File.Exists(filePath)) File.Delete(filePath);
            } catch (System.IO.IOException) {
                // locked files will not be deleted, just ignore and continue
            }
            _dirManager.RegisterDeleteIfItExists(fileKey);
        }
    }
    public bool DoesNotExistOrIsEmpty(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_dirManager) {
            var filePath = Path.Combine(_baseFolder, fileKey);
            return !File.Exists(filePath) || new FileInfo(filePath).Length == 0;
        }
    }
    public FileMeta[] GetFiles() {
        lock (_dirManager) return _dirManager.GetFiles();
    }
    public long GetFileSizeOrZeroIfUnknown(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_dirManager) {
            return _dirManager.GetFileSizeOrZeroIfUnknown(fileKey);
        }
    }
    public void MoveFile(IODisk sourceIo, string sourceFileKey, string destFileKey, bool overwrite) {
        FileKeyUtility.ValidateFileKeyString(sourceFileKey);
        FileKeyUtility.ValidateFileKeyString(destFileKey);
        lock (_dirManager) {
            var source = Path.Combine(sourceIo._baseFolder, sourceFileKey);
            var dest = Path.Combine(_baseFolder, destFileKey);
            if (overwrite) DeleteIfItExists(destFileKey);
            if (_dirManager.Exists(destFileKey)) throw new Exception($"File {destFileKey} already exists");
            File.Move(source, dest);
        }
    }
    public void RenameFile(string fileKey, string newFileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        FileKeyUtility.ValidateFileKeyString(newFileKey);
        lock (_dirManager) {
            var filePath = Path.Combine(_baseFolder, fileKey);
            var newFilePath = Path.Combine(_baseFolder, newFileKey);
            if (File.Exists(newFilePath)) throw new Exception($"File {newFileKey} already exists");
            _dirManager.ValidateRename(fileKey, newFileKey);
            File.Move(filePath, newFilePath);
            _dirManager.Rename(fileKey, newFileKey);
        }
    }
    public bool CanRenameFile => true;
    public void ResetLocks() {
        lock (_dirManager) _dirManager.ResetLocks();
    }
}