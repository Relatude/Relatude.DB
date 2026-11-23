using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Relatude.DB.Common;
namespace Relatude.DB.IO;

/// <summary>A blob someone holds a lease on, as <see cref="AzureBlobIOProvider.GetLeasedFiles"/> lists it.</summary>
public class LeasedBlob {
    public required string Key { get; init; }
    /// <summary>leased, or breaking while a break with a period runs out.</summary>
    public required string State { get; init; }
    /// <summary>True for a lease that never ends on its own. This provider only ever takes leases that
    /// do; one that does not was taken by an older version of it, or by another tool.</summary>
    public bool Infinite { get; init; }
    /// <summary>Held by this provider for a stream open in this process.</summary>
    public bool HeldHere { get; init; }
}

/// <summary>
/// A blob another process holds a lease on, still after the wait. A file lock like any other, so the
/// open retries and auto-recovery treat it as one; its own type so the admin UI can offer to break it.
/// </summary>
public class BlobLeasedException(string message, Exception? innerException, string blobName, bool infinite)
    : FileLockedException(message, innerException) {
    public string BlobName { get; } = blobName;
    /// <summary>A lease that never ends by itself: nobody is going to let go of it.</summary>
    public bool Infinite { get; } = infinite;
}

/// <summary>
/// IIOProvider for Azure Blob Storage over plain HttpClient (no Azure SDK), using append blobs.
/// Supports account key and SAS connection strings, and UseDevelopmentStorage=true for Azurite.
/// <para><b>Open files.</b> Every provider on the same container in this process shares one table of the files
/// open in it: a file open for writing cannot be opened again, a file open for reading can be read by any number
/// of streams but not written, and a file open either way cannot be deleted.</para>
/// <para><b>The lock.</b> With lockBlob the container is locked against other processes - another instance, or
/// the next process of an overlapped recycle: a lease on one blob, "_lock", taken by the first write and let go
/// by <see cref="CloseAllOpenStreams"/> once nothing is being written, which a database does as it closes (see
/// <see cref="AzureBlobContainerLock"/>). Reads take no lock: bytes once appended never change. Older versions
/// leased every blob they wrote, some of them for ever; <see cref="GetLeasedFiles"/> finds such leases and
/// <see cref="BreakLeases"/> breaks them.</para>
/// </summary>
public class AzureBlobIOProvider : IIOProvider {
    const string _virtualFolderChar = "/";
    string getAndValidateBlobName(string[] path) {
        FileKeyUtility.ValidateFileKeyPath(path);
        return string.Join(_virtualFolderChar, path);
    }

    internal readonly AzureBlobRestClient Client;
    readonly bool _lockBlob;
    // An entry goes with its last stream. Nothing else is kept: what exists, and how big, is asked of the
    // service, the only one that knows what another instance wrote since.
    sealed class OpenFile { public int Readers; public int Writers; }
    sealed class Shared {
        public readonly object Sync = new();
        public readonly Dictionary<string, OpenFile> OpenFiles = new(StringComparer.OrdinalIgnoreCase);
        public readonly AzureBlobContainerLock Lock = new();
    }
    static readonly ConcurrentDictionary<string, Shared> _shared = new(StringComparer.OrdinalIgnoreCase);
    readonly Shared _container;
    readonly Dictionary<string, OpenFile> _openFiles;
    readonly object _lock;
    readonly List<IStream> _openStreams = new();
    IOCounters _closedStreams;
    public IOCounters Counters {
        get {
            lock (_lock) {
                var total = _closedStreams;
                foreach (var stream in _openStreams) total += stream.Counters;
                return total;
            }
        }
    }
    public AzureBlobIOProvider(string blobContainerName, string blobConnectionString, bool lockBlob)
        : this(blobContainerName, blobConnectionString, lockBlob, "") { }
    // a scope of its own stands in for another process in tests
    internal AzureBlobIOProvider(string blobContainerName, string blobConnectionString, bool lockBlob, string processScope) {
        Client = new AzureBlobRestClient(blobConnectionString, blobContainerName);
        _lockBlob = lockBlob;
        _container = _shared.GetOrAdd(processScope + "|" + Client.ContainerUrl, _ => new());
        _openFiles = _container.OpenFiles;
        _lock = _container.Sync;
        Client.CreateContainerIfNotExists();
        // one blob, not the container: a connection that may not list fails here rather than at its first listing
        Client.ListBlobs(null, maxResults: 1);
    }
    /// <summary>How long a write waits for another process to let go of the container.</summary>
    internal TimeSpan LockWait { get; set; } = AzureBlobContainerLock.DefaultWait;
    AzureBlobContainerLock.Lease? enterLock() => _lockBlob ? _container.Lock.Enter(Client, LockWait) : null;
    void exitLock(AzureBlobContainerLock.Lease? lease) {
        if (lease != null) _container.Lock.Exit(lease);
    }

    // ---- leases ----

    /// <summary>
    /// Every blob in this container that someone holds a lease on, below <paramref name="folder"/> when
    /// one is given: the container's lock, and leases older versions left on the blobs they wrote.
    /// </summary>
    public LeasedBlob[] GetLeasedFiles(string[]? folder = null) {
        var prefix = folder is { Length: > 0 } ? getAndValidateBlobName(folder) + _virtualFolderChar : null;
        return [.. Client.ListBlobs(prefix)
            .Where(b => b.LeaseState is "leased" or "breaking")
            .Select(b => new LeasedBlob {
                Key = b.Name,
                State = b.LeaseState!,
                Infinite = string.Equals(b.LeaseDuration, "infinite", StringComparison.OrdinalIgnoreCase),
                HeldHere = heldHere(b.Name),
            })];
    }
    bool heldHere(string blobName) => blobName == AzureBlobContainerLock.BlobName && _container.Lock.HeldHere;
    /// <summary>
    /// Breaks the leases on the given blobs at once, whoever holds them - except this process's own lock,
    /// which is skipped. Breaking the lock of a process that is still running lets two processes write the
    /// container, so this is for a lock whose holder is known to be gone, and for leases nobody is going to
    /// let go of.
    /// </summary>
    public (int Broken, int Skipped, string[] Errors) BreakLeases(IEnumerable<string> keys) {
        int broken = 0, skipped = 0;
        var errors = new List<string>();
        foreach (var key in keys) {
            if (heldHere(key)) {
                skipped++;
                continue;
            }
            try {
                Client.BreakLease(key);
                broken++;
            } catch (AzureBlobRequestException err) when (err.StatusCode == 409 && err.ErrorCode == "LeaseNotPresentWithLeaseOperation") {
                skipped++; // let go of, or ended, since it was listed
            } catch (Exception err) {
                errors.Add(key + ": " + err.Message);
            }
        }
        return (broken, skipped, [.. errors]);
    }

    // ---- files ----

    // both under _lock
    OpenFile openFileOf(string blobName) {
        if (!_openFiles.TryGetValue(blobName, out var open)) _openFiles.Add(blobName, open = new());
        return open;
    }
    void forgetIfClosed(string blobName, OpenFile open) {
        if (open.Readers == 0 && open.Writers == 0 && _openFiles.TryGetValue(blobName, out var current) && current == open) _openFiles.Remove(blobName);
    }
    // a listed blob as the listing says, with the streams this process has open on it; one being written
    // is as long as its stream, which counts what has not been sent yet. Under _lock.
    FileMeta metaOf(BlobListItem blob, Dictionary<string, IStream> writing) {
        var meta = new FileMeta { Key = blob.Name, Size = blob.ContentLength, LastModifiedUtc = blob.LastModifiedUtc, CreationTimeUtc = blob.CreatedOnUtc };
        if (_openFiles.TryGetValue(blob.Name, out var open)) {
            meta.Readers = open.Readers;
            meta.Writers = open.Writers;
        }
        if (writing.TryGetValue(blob.Name, out var stream)) meta.Size = stream.Length;
        return meta;
    }
    Dictionary<string, IStream> writingStreams() {
        var writing = new Dictionary<string, IStream>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in _openStreams) if (stream is IAppendStream) writing.TryAdd(stream.FileKey, stream);
        return writing;
    }
    FileMeta[] metasOf(List<BlobListItem> blobs) {
        lock (_lock) {
            var writing = writingStreams();
            return [.. blobs.Select(b => metaOf(b, writing))];
        }
    }

    public IReadStream OpenRead(string[] path, long position) {
        var blobName = getAndValidateBlobName(path);
        return openRead(position, blobName);
    }
    public bool Exists(string[] path) {
        var blobName = getAndValidateBlobName(path);
        return Client.GetProperties(blobName) != null;
    }
    // The count of readers or writers is taken first, under the lock, so a second open cannot slip past
    // the checks; the calls to the service come after, outside it, as taking the container's lock can wait.
    // Whatever fails on the way gives back what was taken by then, or the file would stay "locked" in this
    // process, and the container locked for everyone else, until the process ends.
    IReadStream openRead(long position, string blobName) {
        OpenFile open;
        lock (_lock) {
            open = openFileOf(blobName);
            if (open.Writers > 0) throw new Exception($"File {blobName} is locked for writing. ");
            open.Readers++;
        }
        AzureBlobIOReadStream? stream = null;
        try {
            var properties = Client.GetProperties(blobName) ?? throw new Exception($"File {blobName} does not exist");
            stream = new AzureBlobIOReadStream(Client, blobName, position, properties, () => {
                lock (_lock) {
                    open.Readers--;
                    forgetIfClosed(blobName, open);
                    _closedStreams += stream!.Counters;
                    _openStreams.Remove(stream!);
                }
            });
        } catch {
            lock (_lock) {
                open.Readers--;
                forgetIfClosed(blobName, open);
            }
            throw;
        }
        lock (_lock) {
            _openStreams.Add(stream);
        }
        return stream;
    }
    public IAppendStream OpenAppend(string[] path) {
        var blobName = getAndValidateBlobName(path);
        return openAppend(blobName);
    }
    IAppendStream openAppend(string fileKey) {
        OpenFile open;
        lock (_lock) {
            open = openFileOf(fileKey);
            if (open.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
            if (open.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
            open.Writers++;
        }
        AzureBlobContainerLock.Lease? lease = null;
        AzureBlobIOAppendStream? stream = null;
        try {
            lease = enterLock();
            Client.CreateAppendBlobIfNotExists(fileKey);
            stream = new AzureBlobIOAppendStream(Client, fileKey, lease, _ => {
                try {
                    exitLock(lease);
                } finally {
                    lock (_lock) {
                        open.Writers--;
                        forgetIfClosed(fileKey, open);
                        _closedStreams += stream!.Counters;
                        _openStreams.Remove(stream!);
                    }
                }
            });
        } catch {
            exitLock(lease);
            lock (_lock) {
                open.Writers--;
                forgetIfClosed(fileKey, open);
            }
            throw;
        }
        lock (_lock) {
            _openStreams.Add(stream);
        }
        return stream;
    }
    public void DeleteFileIfItExists(string[] path) {
        var blobName = getAndValidateBlobName(path);
        deleteFileIfItExists(blobName);
    }
    void deleteFileIfItExists(string fileKey) {
        lock (_lock) {
            if (_openFiles.TryGetValue(fileKey, out var open)) {
                if (open.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
                if (open.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
            }
        }
        var lease = enterLock();
        try {
            lease?.EnsureWritable(fileKey);
            Client.DeleteBlobIfExists(fileKey);
        } catch (AzureBlobRequestException err) when (err.ErrorCode == "LeaseIdMissing") {
            // an older version leased it. One that never ends was left behind and nobody is going to let go
            // of it, so it is broken, as that version did; any other belongs to a process still using the file
            var duration = Client.GetProperties(fileKey)?.LeaseDuration;
            if (!string.Equals(duration, "infinite", StringComparison.OrdinalIgnoreCase)) {
                throw new BlobLeasedException("\"" + fileKey + "\" cannot be deleted: another process holds a lease on it. ", err, fileKey, false);
            }
            Client.BreakLease(fileKey);
            Client.DeleteBlobIfExists(fileKey);
        } finally {
            exitLock(lease);
        }
    }
    public bool DoesNotExistOrIsEmpty(string[] path) {
        var blobName = getAndValidateBlobName(path);
        var properties = Client.GetProperties(blobName);
        if (properties == null) return true;
        return properties.ContentLength == 0;
    }
    public FileMeta[] GetFiles() => metasOf(listBlobs(null));
    // Blob names are case sensitive, so unlike the other providers the folder must match in case.
    public FileMeta[] GetFiles(string[] folder) => GetFiles(folder, recursive: true);
    public FileMeta[] GetFiles(string[] folder, bool recursive) {
        var prefix = folder.Length == 0 ? null : getAndValidateBlobName(folder) + _virtualFolderChar;
        return metasOf(listBlobs(prefix, oneLevel: !recursive));
    }
    // the container's lock is the provider's own, not a file
    List<BlobListItem> listBlobs(string? prefix, bool oneLevel = false) {
        var blobs = Client.ListBlobs(prefix, oneLevel);
        if (string.IsNullOrEmpty(prefix)) blobs.RemoveAll(b => b.Name == AzureBlobContainerLock.BlobName);
        return blobs;
    }
    public long GetFileSizeOrZeroIfUnknown(string[] path) {
        var blobName = getAndValidateBlobName(path);
        return getFileSizeOrZeroIfUnknown(blobName);
    }
    long getFileSizeOrZeroIfUnknown(string blobName) {
        return Client.GetProperties(blobName)?.ContentLength ?? 0;
    }
    public Task<IOBenchmarkReport> BenchmarkPerformanceAsync(int testDurationInMs = 5000, IProgress<IOBenchmarkProgress>? progress = null, CancellationToken cancellationToken = default) {
        List<string> notes = [
            "Every read, write and lookup is a request to the storage service, so the time a small operation takes is mostly the round trip between this server and the storage account's region.",
            "Appends wait in memory until the stream is flushed or holds 20 MB and are then sent as one block. A small read fetches 100 KB and a read stream 1 MB at a time, so reads close to the previous one are served from what it fetched.",
        ];
        if (_lockBlob) notes.Add("The container is locked against other processes while this one writes it (LockBlob): one lease, taken by the first write and renewed every 5 s. Reads take no lock.");
        return IOBenchmark.RunAsync(this, new IOBenchmarkOptions {
            Kind = "Azure Blob Storage",
            Location = Client.ContainerUrl,
            ReadFileBytes = IOBenchmarkOptions.ScaledBytes(16L * IOBenchmarkOptions.MB, testDurationInMs, 64L * IOBenchmarkOptions.MB),
            RecycleFileBytes = 16L * IOBenchmarkOptions.MB,
            DurableFlushBytes = 8L * IOBenchmarkOptions.MB,
            Notes = notes,
        }, testDurationInMs, progress, cancellationToken);
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
    /// <summary>Closes every stream, each on its own: one that fails to flush still lets go of its file,
    /// and does not keep the ones after it open.</summary>
    public void CloseAllOpenStreams() {
        IStream[] streams;
        lock (_lock) {
            streams = _openStreams.ToArray();
        }
        List<Exception>? errors = null;
        foreach (var stream in streams) {
            try {
                stream.Dispose();
            } catch (Exception err) {
                (errors ??= []).Add(err);
            }
        }
        if (_lockBlob) _container.Lock.ReleaseIfIdle();
        if (errors != null) throw new AggregateException("Not every stream closed cleanly. ", errors);
    }

    public void DeleteFolderIfItExists(string[] path) {
        var prefix = getAndValidateBlobName(path) + _virtualFolderChar;
        foreach (var blob in Client.ListBlobs(prefix)) deleteFileIfItExists(blob.Name);
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
        var blobs = listBlobs(prefix.Length > 0 ? prefix : null);
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
            lock (_lock) { // the streams open on them change as they open and close
                var writing = writingStreams();
                folder.Files = [.. files.Select(blob => metaOf(blob, writing))];
            }
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
