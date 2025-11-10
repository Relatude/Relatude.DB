using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
namespace Relatude.DB.IO;
public class AzureBlobIOProvider : IIOProvider {
    readonly BlobContainerClient _container;
    readonly bool _lockBlob;
    readonly Dictionary<string, FileMeta> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly object _lock = new();
    readonly List<IStream> _openStreams = new();
    public AzureBlobIOProvider(string blobContainerName, string blobConnectionString, bool lockBlob) {
        var blobContainer = new BlobContainerClient(blobConnectionString, blobContainerName);
        _container = blobContainer;
        _lockBlob = lockBlob;
        syncDirInfo(_container.GetBlobs().ToArray());
    }

    static string leasePath(string fileKey) => Environment.ProcessPath + "_" + fileKey + "_leaseId";
    static internal void DeleteLastLeaseId(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        if (File.Exists(leasePath(fileKey))) File.Delete(leasePath(fileKey));
    }
    static internal void SaveLastLeaseId(string fileKey, string leaseId) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        if (File.Exists(leasePath(fileKey))) File.Delete(leasePath(fileKey));
        File.WriteAllText(leasePath(fileKey), leaseId);
    }
    static internal void EnsureResetOfLeaseId(BlobContainerClient container, string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        if (!File.Exists(leasePath(fileKey))) return;
        var leaseId = File.ReadAllText(leasePath(fileKey));
        var client = container.GetBlobClient(fileKey);
        try {
            if (client.Exists()) {
                var leaseClient = client.GetBlobLeaseClient(leaseId);
                leaseClient.Release();
            }
        } catch {
            try {
                var leaseClient = client.GetBlobLeaseClient(leaseId);
                leaseClient.Break();
            } catch {
            }
        }
    }
    void syncDirInfo(BlobItem[] existing) {
        foreach (var f in existing) {
            if (!_files.ContainsKey(f.Name)) {
                _files.Add(f.Name, new FileMeta {
                    Key = f.Name,
                    Size = f.Properties.ContentLength.HasValue ? f.Properties.ContentLength.Value : 0,
                    LastModifiedUtc = f.Properties.LastModified.HasValue ? f.Properties.LastModified.Value.UtcDateTime : DateTime.MinValue,
                    CreationTimeUtc = f.Properties.CreatedOn.HasValue ? f.Properties.CreatedOn.Value.UtcDateTime : DateTime.MinValue,
                });
            }
        }
        var deleted = _files.Keys.Where(k => !existing.Any(f => f.Name == k)).ToArray();
        foreach (var k in deleted) _files.Remove(k);
    }

    public IReadStream OpenRead(string fileKey, long position) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            FileMeta meta;
            if (!_files.TryGetValue(fileKey, out meta!)) throw new Exception($"File {fileKey} does not exist");
            if (meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
            meta.Readers++;
            AzureBlobIOReadStream? stream = null;
            stream = new AzureBlobIOReadStream(_container, fileKey, position, _lockBlob, () => {
                lock (_lock) {
                    meta.Readers--;
                    _openStreams.Remove(stream!);
                }
            });
            _openStreams.Add(stream);
            return stream;
        }
    }
    public IAppendStream OpenAppend(string fileKey) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            FileMeta meta;
            if (!_files.TryGetValue(fileKey, out meta!)) {
                meta = new FileMeta { Key = fileKey };
                _files.Add(fileKey, meta);
            } else {
                if (meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
                if (meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
            }
            meta.Writers++;
            AzureBlobIOAppendStream? stream = null;
            stream = new AzureBlobIOAppendStream(_container, fileKey, fileKey, _lockBlob, (long size) => {
                lock (_lock) {
                    meta.Writers--;
                    meta.LastModifiedUtc = DateTime.UtcNow;
                    meta.Size = size;
                    _openStreams.Remove(stream!);
                }
            });
            _openStreams.Add(stream);
            return stream;
        }
    }
    public void DeleteIfItExists(string fileKey) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            if (_files.TryGetValue(fileKey, out var meta)) {
                if (meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
                if (meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
            }
            EnsureResetOfLeaseId(_container, fileKey);
            _container.DeleteBlobIfExists(fileKey);
            if (_files.TryGetValue(fileKey, out meta)) _files.Remove(fileKey);
        }
    }
    public bool DoesNotExistOrIsEmpty(string fileKey) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            var client = _container.GetBlobClient(fileKey);
            if (!client.Exists()) return true;
            return client.GetProperties().Value.ContentLength == 0;
        }
    }
    public FileMeta[] GetFiles() {
        lock (_lock) {
            var existing = _container.GetBlobs().ToArray();
            syncDirInfo(existing);
            return _files.Values.ToArray();
        }
    }
    public long GetFileSizeOrZeroIfUnknown(string fileKey) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            var client = _container.GetBlobClient(fileKey);
            return client.Exists() ? client.GetProperties().Value.ContentLength : 0;
        }
    }
    public bool CanRenameFile => false;
    public void RenameFile(string fileKey, string newFileKey) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            throw new NotSupportedException();
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
}