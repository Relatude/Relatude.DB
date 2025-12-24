using Relatude.DB.Common;
using System.Text;
namespace Relatude.DB.IO;
public class IOProviderMemory : IIOProvider {
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
            if (!_disk.TryGetValue(fileKey, out var file)) throw new Exception($"File {fileKey} does not exist");
            if (file.Meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
            file.Meta.Readers++;
            StoreStreamMemoryRead? stream = null;
            stream = new StoreStreamMemoryRead(fileKey, file.Bytes, position, () => {
                lock (_lock) {
                    file.Meta.Readers--;
                    _openStreams.Remove(stream!);
                }
            });
            _openStreams.Add(stream);
            return stream;
        }
    }
    public IAppendStream OpenAppend(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_lock) {
            if (!_disk.TryGetValue(fileKey, out var file)) {
                file = new MemFile();
                _disk.Add(fileKey, file);
            } else {
                if (file.Meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
                if (file.Meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
            }
            file.Meta.Writers++;
            MemoryStream ms = new();
            ms.Write(file.Bytes);
            StoreStreamMemoryWrite? stream = null;
            stream = new StoreStreamMemoryWrite(fileKey, ms, ms => {
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
    }
    public void DeleteIfItExists(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_lock) {
            if (_disk.TryGetValue(fileKey, out var file)) {
                if (file.Meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
                if (file.Meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
                _disk.Remove(fileKey);
            }
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
            return _disk.TryGetValue(fileKey, out var f) ? f.Bytes.Length : 0;
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
            if(_openStreams.Count != 0) throw new Exception("Not all streams could be closed. ");
        }
    }
    public bool CanHaveFolders => false;
    public Task<FolderMeta[]> GetFoldersAsync() {
        throw new NotSupportedException("IOProviderMemory does not support subfolders. ");
    }
    public void DeleteFolderIfItExists(string folderName) {
        throw new NotSupportedException("IOProviderMemory does not support subfolders. ");
    }
}
