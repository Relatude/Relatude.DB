using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
namespace Relatude.DB.IO;

public class AzureBlobIOProvider : IIOProvider {
    const string _virtualFolderChar = "/";
    string getAndValidateBlobName(string[] path) {
        FileKeyUtility.ValidateFileKeyPath(path);
        return string.Join(_virtualFolderChar, path);
    }

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
        if (File.Exists(leasePath(fileKey))) File.Delete(leasePath(fileKey));
    }
    static internal void SaveLastLeaseId(string fileKey, string leaseId) {
        if (File.Exists(leasePath(fileKey))) File.Delete(leasePath(fileKey));
        File.WriteAllText(leasePath(fileKey), leaseId);
    }
    static internal void EnsureResetOfLeaseId(BlobContainerClient container, string fileKey) {
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
        long getSize(BlobItem blob) {
            var match = _openStreams.Where(s => s.FileKey == blob.Name).FirstOrDefault();
            if (match != null)
                return match.Length;
            return blob.Properties.ContentLength.HasValue ? blob.Properties.ContentLength.Value : 0;
        }
        foreach (var blob in existing) {
            if (!_files.TryGetValue(blob.Name, out var meta)) {
                _files.Add(blob.Name, new FileMeta {
                    Key = blob.Name,
                    Size = getSize(blob),
                    LastModifiedUtc = blob.Properties.LastModified.HasValue ? blob.Properties.LastModified.Value.UtcDateTime : DateTime.MinValue,
                    CreationTimeUtc = blob.Properties.CreatedOn.HasValue ? blob.Properties.CreatedOn.Value.UtcDateTime : DateTime.MinValue,
                });
            } else {
                meta.Size = getSize(blob);
                meta.LastModifiedUtc = blob.Properties.LastModified.HasValue ? blob.Properties.LastModified.Value.UtcDateTime : DateTime.MinValue;
                meta.CreationTimeUtc = blob.Properties.CreatedOn.HasValue ? blob.Properties.CreatedOn.Value.UtcDateTime : DateTime.MinValue;
            }
        }
        var deleted = _files.Keys.Where(k => !existing.Any(f => f.Name == k)).ToArray();
        foreach (var k in deleted) _files.Remove(k);
    }

    public IReadStream OpenRead(string fileKey, long position) {
        FileKeyUtility.ValidateFileKeyString(fileKey);

        return openRead(position, fileKey);

    }
    public IReadStream OpenRead(string[] path, long position) {
        var blobName = getAndValidateBlobName(path);

        return openRead(position, blobName);

    }
    public bool Exists(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        return _container.GetBlobClient(fileKey).Exists();
    }
    public bool Exists(string[] path) {
        var blobName = getAndValidateBlobName(path);
        return _container.GetBlobClient(blobName).Exists();
    }
    IReadStream openRead(long position, string blobName) {
        //Console.WriteLine("Opening read stream for " + blobName + " at position " + position);
        FileMeta meta;
        lock (_lock) {
            if (!_files.TryGetValue(blobName, out meta!)) throw new Exception($"File {blobName} does not exist");
            if (meta.Writers > 0) throw new Exception($"File {blobName} is locked for writing. ");
            meta.Readers++;
        }
        AzureBlobIOReadStream? stream = null;
        stream = new AzureBlobIOReadStream(_container, blobName, position, _lockBlob, () => {
            lock (_lock) {
                meta.Readers--;
                _openStreams.Remove(stream!);
            }
        });
        lock (_lock) {
            _openStreams.Add(stream);
        }
        return stream;
    }
    public IAppendStream OpenAppend(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_lock) {
            return openAppend(fileKey);
        }
    }
    public IAppendStream OpenAppend(string[] path) {
        var blobName = getAndValidateBlobName(path);
        lock (_lock) {
            return openAppend(blobName);
        }
    }
    IAppendStream openAppend(string fileKey) {
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
                meta.Size = 0;
                _openStreams.Remove(stream!);
            }
        });
        _openStreams.Add(stream);
        return stream;
    }
    public void DeleteFileIfItExists(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        deleteFileIfItExists(fileKey);
    }
    public void DeleteFileIfItExists(string[] path) {
        var blobName = getAndValidateBlobName(path);
        deleteFileIfItExists(blobName);
    }
    void deleteFileIfItExists(string fileKey) {
        FileMeta? meta;
        lock (_lock) {
            if (_files.TryGetValue(fileKey, out meta)) {
                if (meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
                if (meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
            }
        }
        EnsureResetOfLeaseId(_container, fileKey);
        _container.DeleteBlobIfExists(fileKey);
        lock (_lock) {
            if (_files.TryGetValue(fileKey, out meta)) _files.Remove(fileKey);
        }
    }
    public bool DoesNotExistOrIsEmpty(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        var client = _container.GetBlobClient(fileKey);
        if (!client.Exists()) return true;
        return client.GetProperties().Value.ContentLength == 0;
    }
    public FileMeta[] GetFiles() {
        var existing = _container.GetBlobs().ToArray();
        lock (_lock) {
            syncDirInfo(existing);
            return _files.Values.ToArray();
        }
    }
    public long GetFileSizeOrZeroIfUnknown(string fileKey) {
        FileKeyUtility.ValidateFileKeyString(fileKey);
        lock (_lock) {
            return getFileSizeOrZeroIfUnknown(fileKey);
        }
    }
    public long GetFileSizeOrZeroIfUnknown(string[] path) {
        var blobName = getAndValidateBlobName(path);
        return getFileSizeOrZeroIfUnknown(blobName);
    }
    long getFileSizeOrZeroIfUnknown(string blobName) {
        var client = _container.GetBlobClient(blobName);
        return client.Exists() ? client.GetProperties().Value.ContentLength : 0;
    }
    public bool CanRenameFile => false;
    public void RenameFile(string fileKey, string newFileKey) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyString(fileKey);
            throw new NotSupportedException();
        }
    }
    public void CloseAllOpenStreams() {
        IStream[] streams;
        lock (_lock) {
            streams = _openStreams.ToArray();
        }
        foreach (var stream in streams) {
            stream.Dispose();
        }
    }

}