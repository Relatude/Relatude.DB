namespace Relatude.DB.IO;
internal class DirectoryManager(Func<FileMetaLight[]> getFiles) {
    readonly Dictionary<string, FileMeta> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<FileMetaLight[]> _getFiles = getFiles;
    public void Reset() {
        _files.Clear();
        refresh();
    }
    public bool Exists(string fileKey) {
        refresh();
        return _files.ContainsKey(fileKey);
    }
    void refresh() {
        var existing = _getFiles();
        foreach (var f in existing) {
            if (_files.TryGetValue(f.Key, out var meta)) {
                meta.Size = f.Size;
                meta.LastModifiedUtc = f.LastModifiedUtc;
                meta.CreationTimeUtc = f.CreationTimeUtc;
            } else {
                _files.Add(f.Key, new() {
                    Key = f.Key,
                    Size = f.Size,
                    LastModifiedUtc = f.LastModifiedUtc,
                    CreationTimeUtc = f.CreationTimeUtc,
                });
            }
        }
        var deleted = _files.Keys.Where(k => !existing.Any(f => f.Key == k)).ToArray();
        foreach (var k in deleted) _files.Remove(k);
    }
    public void RegisterReader(string fileKey) {
        refresh();
        if (!_files.TryGetValue(fileKey, out var meta)) throw new Exception($"File {fileKey} does not exist");
        if (meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
        meta.Readers++;
    }
    public void DeRegisterReader(string fileKey) {
        refresh();
        if (!_files.TryGetValue(fileKey, out var meta)) throw new Exception($"File {fileKey} does not exist");
        if (meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
        if (meta.Readers == 0) throw new Exception($"File {fileKey} is not locked for reading. ");
        meta.Readers--;
    }
    public void ValidateWriter(string fileKey) {
        refresh();
        if (!_files.TryGetValue(fileKey, out var meta)) return;
        if (meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
        if (meta.Writers > 0) throw new Exception($"File {fileKey} is already locked for writing. ");
    }
    public void RegisterWriter(string fileKey) {
        refresh();
        if (!_files.TryGetValue(fileKey, out var meta)) throw new Exception($"File {fileKey} does not exist");
        if (meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
        if (meta.Writers > 0) throw new Exception($"File {fileKey} is already locked for writing. ");
        meta.Writers++;
    }
    public void DeRegisterWriter(string fileKey) {
        refresh();
        if (!_files.TryGetValue(fileKey, out var meta)) throw new Exception($"File {fileKey} does not exist");
        if (meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
        if (meta.Writers == 0) throw new Exception($"File {fileKey} is not locked for writing. ");
        meta.Writers--;
    }
    public void RegisterDeleteIfItExists(string fileKey) {
        refresh();
        if (!_files.TryGetValue(fileKey, out var meta)) return;
        if (meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
        if (meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
        _files.Remove(fileKey);
    }
    public void ValidateCanDeleteIfItExists(string fileKey) {
        refresh();
        if (!_files.TryGetValue(fileKey, out var meta)) return;
        if (meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
        if (meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
    }
    public FileMeta[] GetFiles() {
        refresh();
        return [.. _files.Values];
    }
    public void Rename(string fileKey, string newFileKey) {
        refresh();
        if (_files.TryGetValue(fileKey, out var meta)) {
            _files.Remove(fileKey);
            meta.Key = newFileKey;
            _files.Add(newFileKey, meta);
        }
    }
    public void ValidateRename(string fileKey, string newFileKey) {
        refresh();
        if (_files.ContainsKey(newFileKey)) throw new Exception($"File {newFileKey} already exists");
        if (!_files.TryGetValue(fileKey, out var meta)) throw new Exception($"File {fileKey} does not exist");
        if (meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
        if (meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
    }
    public long GetFileSizeOrZeroIfUnknown(string fileKey) {
        refresh();
        return _files.TryGetValue(fileKey, out var meta) ? meta.Size : 0;
    }
    public void ResetLocks() {
        refresh();
        foreach (var meta in _files.Values) {
            meta.Readers = 0;
            meta.Writers = 0;
        }
    }
}
