using Relatude.DB.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
namespace Relatude.DB.IO;

public class IOProviderMemory : IIOProvider {
    const string _virtualFolderChar = "/";
    string getAndValidateName(string[] path) {
        FileKeyUtility.ValidateFileKeyPath(path);
        return string.Join(_virtualFolderChar, path);
    }
    class MemFile {
        public byte[] Bytes = [];
        public FileMeta Meta = new();
    }
    readonly object _lock = new();
    readonly List<IStream> _openStreams = [];
    readonly Dictionary<string, MemFile> _disk = new(StringComparer.OrdinalIgnoreCase);
    public void AddCorruption(string fileKey, long from, int length) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_lock) {
            if (!_disk.TryGetValue(fileKey, out var file)) throw new Exception($"File {fileKey} does not exist");
            if (file.Meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
            if (file.Meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
            var data = _disk[fileKey];
            // add random data to the arrey at the range specified:
            var random = new Random();
            for (int i = 0; i < length; i++) {
                file.Bytes[from + i] = (byte)random.Next(0, 255);
            }
        }
    }
    public IReadStream OpenRead(string fileKey, long position) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_lock) {
            return openRead(fileKey, position);
        }
    }
    public IReadStream OpenRead(string[] path, long position) {
        var fileName = getAndValidateName(path);
        lock (_lock) {
            return openRead(fileName, position);
        }
    }
    public bool Exists(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_lock) {
            return _disk.ContainsKey(fileKey);
        }
    }
    public bool Exists(string[] path) {
        var fileName = getAndValidateName(path);
        FileKeyUtility.ValidateFileKeyString(fileName);
        lock (_lock) {
            return _disk.ContainsKey(fileName);
        }
    }
    IReadStream openRead(string fileName, long position) {
        if (!_disk.TryGetValue(fileName, out var file)) throw new Exception($"File {fileName} does not exist");
        if (file.Meta.Writers > 0) throw new Exception($"File {fileName} is locked for writing. ");
        file.Meta.Readers++;
        StoreStreamMemoryRead? stream = null;
        stream = new StoreStreamMemoryRead(fileName, file.Bytes, position, () => {
            lock (_lock) {
                file.Meta.Readers--;
                _openStreams.Remove(stream!);
            }
        });
        _openStreams.Add(stream);
        return stream;
    }
    public IAppendStream OpenAppend(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_lock) {
            return openAppend(fileKey);
        }
    }
    public IAppendStream OpenAppend(string[] path) {
        var fileKey = getAndValidateName(path);
        lock (_lock) {
            return openAppend(fileKey);
        }
    }
    IAppendStream openAppend(string fileName) {
        if (!_disk.TryGetValue(fileName, out var file)) {
            file = new MemFile();
            _disk.Add(fileName, file);
        } else {
            if (file.Meta.Readers > 0) throw new Exception($"File {fileName} is locked for reading. ");
            if (file.Meta.Writers > 0) throw new Exception($"File {fileName} is locked for writing. ");
        }
        file.Meta.Writers++;
        MemoryStream ms = new();
        ms.Write(file.Bytes);
        StoreStreamMemoryWrite? stream = null;
        stream = new StoreStreamMemoryWrite(fileName, ms, ms => {
            lock (_lock) {
                file.Meta.Writers--;
                file.Meta.LastModifiedUtc = DateTime.UtcNow;
                file.Meta.Size = ms.Length;
                file.Bytes = ms.ToArray();
                ms.Dispose();
                _openStreams.Remove(stream!);
            }
        });
        _openStreams.Add(stream);
        return stream;
    }
    public void DeleteFileIfItExists(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_lock) {
            deleteFileIfItExists(fileKey);
        }
    }
    public void DeleteFileIfItExists(string[] path) {
        var fileName = getAndValidateName(path);
        lock (_lock) {
            deleteFileIfItExists(fileName);
        }
    }
    void deleteFileIfItExists(string fileName) {
        if (_disk.TryGetValue(fileName, out var file)) {
            if (file.Meta.Readers > 0) throw new Exception($"File {fileName} is locked for reading. ");
            if (file.Meta.Writers > 0) throw new Exception($"File {fileName} is locked for writing. ");
            _disk.Remove(fileName);
        }
    }
    public bool DoesNotExistOrIsEmpty(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_lock) {
            if (_disk.TryGetValue(fileKey, out var file)) {
                if (file.Meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
                if (file.Meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
                return file.Bytes.Length == 0;
            } else {
                return true;
            }
        }
    }
    public FileMeta[] GetFiles() {
        lock (_lock) {
            foreach (var file in _disk.Values) file.Meta.Size = file.Bytes.Length;
            return _disk.Select(f => f.Value.Meta).ToArray();
        }
    }
    public long GetFileSizeOrZeroIfUnknown(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_lock) {
            return getFileSizeOrZeroIfUnknown(fileKey);
        }
    }
    public long GetFileSizeOrZeroIfUnknown(string[] path) {
        var fileKey = getAndValidateName(path);
        lock (_lock) {
            return getFileSizeOrZeroIfUnknown(fileKey);
        }
    }
    long getFileSizeOrZeroIfUnknown(string fileName) {
        return _disk.TryGetValue(fileName, out var f) ? f.Bytes.Length : 0;
    }
    public override string ToString() {
        lock (_lock) {
            var sb = new StringBuilder();
            var files = GetFiles();
            foreach (var file in files) {
                sb.AppendLine($"{file.Key.FixedLeft(45)} : {file.Size.InKB().FixedRight(15)}");
            }
            sb.AppendLine();
            var totalSize = files.Sum(f => f.Size);
            sb.AppendLine($"{_disk.Count} files. " + totalSize.InKB());
            return sb.ToString();
        }
    }
    public bool CanRenameFile => true;
    public void RenameFile(string fileKey, string newFileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_lock) {
            if (!_disk.TryGetValue(fileKey, out var file)) throw new Exception($"File {fileKey} does not exist");
            if (file.Meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
            if (file.Meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
            _disk.Remove(fileKey);
            file.Meta.Key = newFileKey;
            _disk.Add(newFileKey, file);
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

    public bool TryGetLocalFilePath(string[] path, [MaybeNullWhen(false)] out string localFilePath) { localFilePath = null; return false; }
    public bool TryGetLocalFolderPath(string[] path, [MaybeNullWhen(false)] out string localFolderPath) { localFolderPath = null; return false; }
    public bool TryMoveIfSameDrive(string fromLocalFilePath, string[] destination) => false;

    public void DeleteFolderIfItExists(string[] path) {
        FileKeyUtility.ValidateFileKeyPath(path);
        var prefix = string.Join(_virtualFolderChar, path) + _virtualFolderChar;
        lock (_lock) {
            var keys = _disk.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var key in keys) {
                var file = _disk[key];
                if (file.Meta.Readers > 0) throw new Exception($"File {key} is locked for reading.");
                if (file.Meta.Writers > 0) throw new Exception($"File {key} is locked for writing.");
                _disk.Remove(key);
            }
        }
    }
    public void EnsureFolder(string[] path) {
        FileKeyUtility.ValidateFileKeyPath(path);
        // Memory provider uses virtual folders via key prefixes; no-op needed.
    }
    public Task<FolderMeta[]> GetFoldersAsync(string[] path, bool recursive, bool withFiles) {
        FileKeyUtility.ValidateFileKeyPath(path);
        var prefix = path.Length > 0 ? string.Join(_virtualFolderChar, path) + _virtualFolderChar : string.Empty;
        lock (_lock) {
            var root = buildVirtualFolder("root", prefix, recursive, withFiles);
            return Task.FromResult(root.SubFolders);
        }
    }
    FolderMeta buildVirtualFolder(string name, string prefix, bool recursive, bool withFiles) {
        var folder = new FolderMeta { Name = name };
        var directChildren = _disk.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..])
            .ToList();
        var subFolderNames = directChildren
            .Where(k => k.Contains(_virtualFolderChar))
            .Select(k => k[..k.IndexOf(_virtualFolderChar)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var directFiles = directChildren.Where(k => !k.Contains(_virtualFolderChar)).ToList();
        folder.HasFiles = directFiles.Count > 0;
        folder.HasSubFolders = subFolderNames.Count > 0;
        if (withFiles) folder.Files = [.. directFiles.Select(f => _disk[prefix + f].Meta)];
        folder.SubFolders = [.. subFolderNames.Select(sf => {
            var sub = recursive
                ? buildVirtualFolder(sf, prefix + sf + _virtualFolderChar, recursive, withFiles)
                : new FolderMeta { Name = sf };
            return sub;
        })];
        return folder;
    }
}
