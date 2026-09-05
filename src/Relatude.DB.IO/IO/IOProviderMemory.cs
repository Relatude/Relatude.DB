using Relatude.DB.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
namespace Relatude.DB.IO;

public class IOProviderMemory : IIOProvider {
    const string _virtualFolderChar = "/";
    // internally files are stored under the joined ('/'-separated) form of their key
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
    public void AddCorruption(string[] path, long from, int length) {
        var fileName = getAndValidateName(path);
        lock (_lock) {
            if (!_disk.TryGetValue(fileName, out var file)) throw new Exception($"File {fileName} does not exist");
            if (file.Meta.Writers > 0) throw new Exception($"File {fileName} is locked for writing. ");
            if (file.Meta.Readers > 0) throw new Exception($"File {fileName} is locked for reading. ");
            // add random data to the array at the range specified:
            var random = new Random();
            for (int i = 0; i < length; i++) {
                file.Bytes[from + i] = (byte)random.Next(0, 255);
            }
        }
    }
    public IReadStream OpenRead(string[] path, long position) {
        var fileName = getAndValidateName(path);
        lock (_lock) {
            return openRead(fileName, position);
        }
    }
    public bool Exists(string[] path) {
        var fileName = getAndValidateName(path);
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
    public IAppendStream OpenAppend(string[] path) {
        var fileKey = getAndValidateName(path);
        lock (_lock) {
            return openAppend(fileKey);
        }
    }
    IAppendStream openAppend(string fileName) {
        if (!_disk.TryGetValue(fileName, out var file)) {
            file = new MemFile();
            file.Meta.Key = fileName;
            file.Meta.CreationTimeUtc = DateTime.UtcNow;
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
    public bool DoesNotExistOrIsEmpty(string[] path) {
        var fileName = getAndValidateName(path);
        lock (_lock) {
            if (_disk.TryGetValue(fileName, out var file)) {
                if (file.Meta.Readers > 0) throw new Exception($"File {fileName} is locked for reading. ");
                if (file.Meta.Writers > 0) throw new Exception($"File {fileName} is locked for writing. ");
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
    public long GetFileSizeOrZeroIfUnknown(string[] path) {
        var fileName = getAndValidateName(path);
        lock (_lock) {
            return _disk.TryGetValue(fileName, out var f) ? f.Bytes.Length : 0;
        }
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
    public bool CanTruncate => true;
    public void TruncateFile(string[] path, long newLength) {
        var fileName = getAndValidateName(path);
        lock (_lock) {
            if (!_disk.TryGetValue(fileName, out var file)) throw new Exception($"File {fileName} does not exist");
            if (file.Meta.Writers > 0) throw new Exception($"File {fileName} is locked for writing. ");
            if (file.Meta.Readers > 0) throw new Exception($"File {fileName} is locked for reading. ");
            if (newLength < 0 || newLength > file.Bytes.Length)
                throw new ArgumentOutOfRangeException(nameof(newLength), $"New length {newLength} is outside the file (0-{file.Bytes.Length}). ");
            file.Bytes = file.Bytes[..(int)newLength];
            file.Meta.Size = newLength;
            file.Meta.LastModifiedUtc = DateTime.UtcNow;
        }
    }
    public void RenameFile(string[] path, string[] newPath) {
        var fileName = getAndValidateName(path);
        var newFileName = getAndValidateName(newPath);
        lock (_lock) {
            if (!_disk.TryGetValue(fileName, out var file)) throw new Exception($"File {fileName} does not exist");
            if (file.Meta.Writers > 0) throw new Exception($"File {fileName} is locked for writing. ");
            if (file.Meta.Readers > 0) throw new Exception($"File {fileName} is locked for reading. ");
            _disk.Remove(fileName);
            file.Meta.Key = newFileName;
            _disk.Add(newFileName, file);
        }
    }
    public bool CanRenameFolder => true;
    public bool SupportsEmptyFolders => false; // folders are key prefixes: one exists while a file has it
    public void RenameFolder(string[] path, string[] newPath) {
        if (path.Length == 0 || newPath.Length == 0) throw new ArgumentException("The storage root cannot be renamed. ");
        var prefix = getAndValidateName(path) + _virtualFolderChar;
        var newPrefix = getAndValidateName(newPath) + _virtualFolderChar;
        lock (_lock) {
            var keys = _disk.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (keys.Count == 0) throw new Exception($"Folder {path.AsKeyString()} does not exist");
            if (_disk.Keys.Any(k => k.StartsWith(newPrefix, StringComparison.OrdinalIgnoreCase))) throw new Exception($"{newPath.AsKeyString()} already exists");
            foreach (var key in keys) {
                var file = _disk[key];
                if (file.Meta.Readers > 0) throw new Exception($"File {key} is locked for reading.");
                if (file.Meta.Writers > 0) throw new Exception($"File {key} is locked for writing.");
            }
            foreach (var key in keys) {
                var file = _disk[key];
                _disk.Remove(key);
                file.Meta.Key = newPrefix + key[prefix.Length..];
                _disk.Add(file.Meta.Key, file);
            }
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
    public Task<FolderMeta> GetFolderAsync(string[] path, bool recursive, bool withFiles) {
        FileKeyUtility.ValidateFileKeyPath(path);
        var prefix = path.Length > 0 ? string.Join(_virtualFolderChar, path) + _virtualFolderChar : string.Empty;
        lock (_lock) {
            return Task.FromResult(buildVirtualFolder(path.Length > 0 ? path[^1] : "", prefix, recursive, withFiles));
        }
    }
    // prefix is the folder's relative path with a trailing delimiter, so trimming it gives the
    // path the well known folder descriptions are keyed on
    static string relPathOfPrefix(string prefix) => prefix.TrimEnd(_virtualFolderChar[0]);
    FolderMeta buildVirtualFolder(string name, string prefix, bool recursive, bool withFiles) {
        var folder = new FolderMeta { Name = name }.Describe(relPathOfPrefix(prefix));
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
            var subPrefix = prefix + sf + _virtualFolderChar;
            if (recursive) return buildVirtualFolder(sf, subPrefix, recursive, withFiles);
            return new FolderMeta {
                Name = sf,
                HasFiles = _disk.Keys.Any(k => k.StartsWith(subPrefix, StringComparison.OrdinalIgnoreCase) && !k[subPrefix.Length..].Contains(_virtualFolderChar)),
                HasSubFolders = _disk.Keys.Any(k => k.StartsWith(subPrefix, StringComparison.OrdinalIgnoreCase) && k[subPrefix.Length..].Contains(_virtualFolderChar)),
            }.Describe(relPathOfPrefix(subPrefix));
        })];
        return folder;
    }
}
