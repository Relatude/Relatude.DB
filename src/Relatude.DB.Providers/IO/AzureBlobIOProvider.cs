using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.IO;

/// <summary>
/// IIOProvider for Azure Blob Storage over plain HttpClient (no Azure SDK), using append blobs.
/// Supports account key and SAS connection strings, and UseDevelopmentStorage=true for Azurite.
/// </summary>
public class AzureBlobIOProvider : IIOProvider {
    const string _virtualFolderChar = "/";
    string getAndValidateBlobName(string[] path) {
        FileKeyUtility.ValidateFileKeyPath(path);
        return string.Join(_virtualFolderChar, path);
    }

    internal readonly AzureBlobRestClient Client;
    readonly bool _lockBlob;
    readonly Dictionary<string, FileMeta> _files = new(StringComparer.OrdinalIgnoreCase);
    readonly object _lock = new();
    readonly List<IStream> _openStreams = new();
    public AzureBlobIOProvider(string blobContainerName, string blobConnectionString, bool lockBlob) {
        Client = new AzureBlobRestClient(blobConnectionString, blobContainerName);
        _lockBlob = lockBlob;
        Client.CreateContainerIfNotExists();
        syncDirInfo(Client.ListBlobs(null));
    }

    static string leasePath(string fileKey) => Environment.ProcessPath + "_" + fileKey + "_leaseId";
    static internal void DeleteLastLeaseId(string fileKey) {
        if (File.Exists(leasePath(fileKey))) File.Delete(leasePath(fileKey));
    }
    static internal void SaveLastLeaseId(string fileKey, string leaseId) {
        if (File.Exists(leasePath(fileKey))) File.Delete(leasePath(fileKey));
        File.WriteAllText(leasePath(fileKey), leaseId);
    }
    static internal void EnsureResetOfLeaseId(AzureBlobRestClient client, string fileKey) {
        if (!File.Exists(leasePath(fileKey))) return;
        var leaseId = File.ReadAllText(leasePath(fileKey));
        try {
            if (client.GetProperties(fileKey) != null) {
                client.ReleaseLease(fileKey, leaseId);
            }
        } catch {
            try {
                client.BreakLease(fileKey);
            } catch {
            }
        }
    }
    void syncDirInfo(List<BlobListItem> existing) {
        long getSize(BlobListItem blob) {
            var match = _openStreams.Where(s => s.FileKey == blob.Name).FirstOrDefault();
            if (match != null)
                return match.Length;
            return blob.ContentLength;
        }
        foreach (var blob in existing) {
            if (!_files.TryGetValue(blob.Name, out var meta)) {
                _files.Add(blob.Name, new FileMeta {
                    Key = blob.Name,
                    Size = getSize(blob),
                    LastModifiedUtc = blob.LastModifiedUtc,
                    CreationTimeUtc = blob.CreatedOnUtc,
                });
            } else {
                meta.Size = getSize(blob);
                meta.LastModifiedUtc = blob.LastModifiedUtc;
                meta.CreationTimeUtc = blob.CreatedOnUtc;
            }
        }
        var deleted = _files.Keys.Where(k => !existing.Any(f => f.Name == k)).ToArray();
        foreach (var k in deleted) _files.Remove(k);
    }

    public IReadStream OpenRead(string[] path, long position) {
        var blobName = getAndValidateBlobName(path);
        return openRead(position, blobName);
    }
    public bool Exists(string[] path) {
        var blobName = getAndValidateBlobName(path);
        return Client.GetProperties(blobName) != null;
    }
    IReadStream openRead(long position, string blobName) {
        FileMeta meta;
        lock (_lock) {
            if (!_files.TryGetValue(blobName, out meta!)) throw new Exception($"File {blobName} does not exist");
            if (meta.Writers > 0) throw new Exception($"File {blobName} is locked for writing. ");
            meta.Readers++;
        }
        AzureBlobIOReadStream? stream = null;
        stream = new AzureBlobIOReadStream(Client, blobName, position, _lockBlob, () => {
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
        stream = new AzureBlobIOAppendStream(Client, fileKey, fileKey, _lockBlob, (long size) => {
            lock (_lock) {
                meta.Writers--;
                meta.LastModifiedUtc = DateTime.UtcNow;
                meta.Size = size; // the folder listing reads this tracked meta, not the blob's properties
                _openStreams.Remove(stream!);
            }
        });
        _openStreams.Add(stream);
        return stream;
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
        EnsureResetOfLeaseId(Client, fileKey);
        Client.DeleteBlobIfExists(fileKey);
        lock (_lock) {
            if (_files.TryGetValue(fileKey, out meta)) _files.Remove(fileKey);
        }
    }
    public bool DoesNotExistOrIsEmpty(string[] path) {
        var blobName = getAndValidateBlobName(path);
        var properties = Client.GetProperties(blobName);
        if (properties == null) return true;
        return properties.ContentLength == 0;
    }
    public FileMeta[] GetFiles() {
        var existing = Client.ListBlobs(null);
        lock (_lock) {
            syncDirInfo(existing);
            return _files.Values.ToArray();
        }
    }
    public long GetFileSizeOrZeroIfUnknown(string[] path) {
        var blobName = getAndValidateBlobName(path);
        return getFileSizeOrZeroIfUnknown(blobName);
    }
    long getFileSizeOrZeroIfUnknown(string blobName) {
        return Client.GetProperties(blobName)?.ContentLength ?? 0;
    }
    public bool CanRenameFile => false;
    public bool CanTruncate => false;
    public void TruncateFile(string[] path, long newLength) {
        throw new NotSupportedException("Azure blob storage cannot truncate a blob in place. ");
    }
    public void RenameFile(string[] path, string[] newPath) {
        lock (_lock) {
            FileKeyUtility.ValidateFileKeyPath(path);
            throw new NotSupportedException();
        }
    }
    public bool CanRenameFolder => false;
    public bool SupportsEmptyFolders => false; // folders are blob name prefixes
    public void RenameFolder(string[] path, string[] newPath) {
        throw new NotSupportedException("Azure blob storage cannot rename a folder: blobs would have to be copied one by one. ");
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

    public void DeleteFolderIfItExists(string[] path) {
        lock (_lock) {
            var prefix = getAndValidateBlobName(path) + _virtualFolderChar;
            var blobsToDelete = Client.ListBlobs(prefix).Select(b => b.Name).ToArray();
            foreach (var blobName in blobsToDelete) {
                deleteFileIfItExists(blobName);
            }
        }
    }
    public bool DeleteFolderIfEmpty(string[] path) {
        if (path.Length == 0) return false;
        // a folder is a blob name prefix, gone with its last blob: there is nothing to delete
        var prefix = getAndValidateBlobName(path) + _virtualFolderChar;
        return !Client.ListBlobs(prefix).Any();
    }
    public void EnsureFolder(string[] path) {
    }
    public Task<FolderMeta> GetFolderAsync(string[] path, bool recursive, bool withFiles) {
        var prefix = path.Length > 0 ? getAndValidateBlobName(path) + _virtualFolderChar : "";
        var blobs = Client.ListBlobs(prefix.Length > 0 ? prefix : null);
        var root = new FolderMeta { Name = path.Length > 0 ? path[^1] : "" }.Describe(relPathOfPrefix(prefix));
        addAzureSubFolders(root, prefix, blobs, recursive, withFiles);
        return Task.FromResult(root);
    }
    // a blob name is the file key, so the prefix (minus its trailing delimiter) is the folder's
    // path below the storage root: what the well known folder descriptions are keyed on
    static string relPathOfPrefix(string prefix) => prefix.TrimEnd(_virtualFolderChar[0]);
    /// <summary>
    /// Sorts the blobs below a folder into its own files and one group per sub folder in a single
    /// pass, and walks on into the groups. It used to go through every blob of the listing once for
    /// each sub folder (and once more for every file it had no tracked entry for), which is fine for
    /// a folder of ten and turns a listing of a store with thousands of folders into minutes.
    /// </summary>
    void addAzureSubFolders(FolderMeta folder, string prefix, List<BlobListItem> blobs, bool recursive, bool withFiles) {
        var files = new List<BlobListItem>();
        // by the name the first blob gave it, as the provider compares keys: ignoring case
        var groups = new Dictionary<string, List<BlobListItem>>(StringComparer.OrdinalIgnoreCase);
        var subFolderNames = new List<string>(); // in listing order, which is name order
        foreach (var blob in blobs) {
            var rel = blob.Name[prefix.Length..];
            if (rel.Length == 0) continue;
            var cut = rel.IndexOf(_virtualFolderChar, StringComparison.Ordinal);
            if (cut < 0) {
                files.Add(blob);
                continue;
            }
            var name = rel[..cut];
            if (!groups.TryGetValue(name, out var group)) {
                groups[name] = group = [];
                subFolderNames.Add(name);
            }
            group.Add(blob);
        }

        if (withFiles) {
            var metas = new FileMeta[files.Count];
            lock (_lock) { // the tracked metas are written by the streams as they open and close
                for (var i = 0; i < files.Count; i++) {
                    var blob = files[i];
                    // prefer the tracked meta (it reflects open streams), else build from the listing
                    metas[i] = _files.TryGetValue(blob.Name, out var tracked) ? tracked
                        : new FileMeta { Key = blob.Name, Size = blob.ContentLength, LastModifiedUtc = blob.LastModifiedUtc, CreationTimeUtc = blob.CreatedOnUtc };
                }
            }
            folder.Files = metas;
        }

        folder.HasFiles = files.Count > 0;
        folder.HasSubFolders = subFolderNames.Count > 0;

        folder.SubFolders = [.. subFolderNames.Select(name => {
            var subPrefix = prefix + name + _virtualFolderChar;
            var subBlobs = groups[name];
            var sub = new FolderMeta { Name = name }.Describe(relPathOfPrefix(subPrefix));
            if (recursive) {
                addAzureSubFolders(sub, subPrefix, subBlobs, recursive, withFiles); // sets both flags as it goes
            } else {
                foreach (var blob in subBlobs) {
                    var rel = blob.Name[subPrefix.Length..];
                    if (rel.Length == 0) continue;
                    if (rel.Contains(_virtualFolderChar)) sub.HasSubFolders = true;
                    else sub.HasFiles = true;
                    if (sub.HasFiles && sub.HasSubFolders) break;
                }
            }
            return sub;
        })];
    }
    public bool TryGetLocalFilePath(string[] path, [MaybeNullWhen(false)] out string localFilePath) { localFilePath = null; return false; }
    public bool TryGetLocalFolderPath(string[] path, [MaybeNullWhen(false)] out string localFolderPath) { localFolderPath = null; return false; }
    public bool TryMoveIfSameDrive(string fromLocalFilePath, string[] destination) => false;
}
