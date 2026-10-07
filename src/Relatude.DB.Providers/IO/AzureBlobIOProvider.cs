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
/// IIOProvider for Azure Blob Storage over plain HttpClient (no Azure SDK), using append blobs.
/// Supports account key and SAS connection strings, and UseDevelopmentStorage=true for Azurite.
/// <para><b>Leases.</b> With lockBlob every blob with an open stream is leased, so that a second
/// process using the same container - another instance, or the next process of an overlapped recycle -
/// cannot write or delete it. A lease is shared by every stream this provider has open on the blob and
/// let go of when the last one closes. Leases run for <see cref="LeaseDurationSeconds"/> and are
/// renewed in the background while they are held, so one left behind by a process that died ends by
/// itself within that time, with nothing to clean up; an open that finds a blob leased waits for it
/// (<see cref="LeaseWaitTimeout"/>), which is long enough to wait such a lease out. Older versions
/// leased for ever and recorded the lease id in a file next to the executable, which failed for any
/// blob in a folder - after the lease was taken - so the lease was stranded and the blob locked for
/// good. Such leases never end; <see cref="GetLeasedFiles"/> finds them and <see cref="BreakLeases"/>
/// breaks them.</para>
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

    // ---- leases ----

    int _leaseDurationSeconds = 60;
    /// <summary>
    /// How long a lease lasts unless it is renewed, which is also how long a blob stays locked after the
    /// process holding it stopped without letting go. 15 to 60 seconds, the range Azure allows; renewed
    /// every third of it.
    /// </summary>
    public int LeaseDurationSeconds {
        get => _leaseDurationSeconds;
        set => _leaseDurationSeconds = Math.Clamp(value, 15, 60);
    }
    /// <summary>How long an open waits for another process to let go of a blob before it gives up. A
    /// little longer than a lease, so the lease of a process that died is waited out.</summary>
    public TimeSpan LeaseWaitTimeout { get; set; } = TimeSpan.FromSeconds(75);

    /// <summary>A lease this provider holds, shared by every stream it has open on the blob.</summary>
    internal sealed class HeldLease(string blobName, string leaseId) {
        public string BlobName { get; } = blobName;
        public string LeaseId { get; } = leaseId;
        /// <summary>The open streams sharing it, changed under the blob's lease lock.</summary>
        public int Users;
        /// <summary>Set once the lease turned out to be gone - broken, or taken by another process after
        /// it expired. Writes then fail instead of going ahead without it.</summary>
        public volatile string? LostReason;
    }
    readonly ConcurrentDictionary<string, HeldLease> _leases = new(StringComparer.OrdinalIgnoreCase);
    // the lease calls of one blob are made one at a time, so taking a lease and letting go of it can
    // never cross; blobs in other stripes do not wait for each other
    readonly object[] _leaseLocks = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    object leaseLockOf(string blobName) => _leaseLocks[(StringComparer.OrdinalIgnoreCase.GetHashCode(blobName) & int.MaxValue) % _leaseLocks.Length];
    // only while a lease is held: a timer left running would keep the provider alive
    Timer? _renewTimer;
    readonly object _renewTimerLock = new();
    int _renewing;
    static void log(string message) => FileOpenRetry.DefaultLog?.Invoke(message);

    /// <summary>
    /// The lease on the blob, shared with any other stream of this provider open on it. When another
    /// process holds one, waits up to <see cref="LeaseWaitTimeout"/> for it to be let go or to end.
    /// </summary>
    /// <exception cref="FileLockedException">Still leased by someone else when the time was up.</exception>
    internal HeldLease AcquireLease(string blobName) {
        return Retry.Run(() => tryAcquireLease(blobName), isLeasedElsewhere, LeaseWaitTimeout,
            onWaitStarted: (_, err) => log("\"" + blobName + "\" is leased by another process, waiting up to "
                + LeaseWaitTimeout.TotalSeconds.ToString("0") + " s for it to let go, or for the lease to end. "),
            onWaitEnded: (attempts, elapsed) => log("\"" + blobName + "\" was let go of after "
                + elapsed.TotalSeconds.ToString("0.0") + " s, leased on attempt " + attempts + "."),
            onExhausted: (err, attempts, elapsed) => new FileLockedException(leasedElsewhereMessage(blobName, elapsed), err));
    }
    // LeaseAlreadyPresent, or LeaseIsBreakingAndCannotBeAcquired while a break with a period runs out
    static bool isLeasedElsewhere(Exception err) => err is AzureBlobRequestException { StatusCode: 409 } e
        && e.ErrorCode != null && e.ErrorCode.StartsWith("Lease", StringComparison.Ordinal);
    HeldLease tryAcquireLease(string blobName) {
        HeldLease held;
        lock (leaseLockOf(blobName)) {
            if (_leases.TryGetValue(blobName, out var current) && current.LostReason == null) {
                current.Users++;
                return current;
            }
            // an id of our own, so a retried request cannot strand a lease the lost attempt took
            var leaseId = Client.AcquireLease(blobName, LeaseDurationSeconds, Guid.NewGuid().ToString());
            held = new HeldLease(blobName, leaseId) { Users = 1 };
            _leases[blobName] = held; // replaces a lost one, which its streams still let go of on their own
        }
        ensureRenewTimer();
        return held;
    }
    string leasedElsewhereMessage(string blobName, TimeSpan waited) {
        string? duration = null;
        try { duration = Client.GetProperties(blobName)?.LeaseDuration; } catch { }
        var text = "\"" + blobName + "\" is leased by another process and still was after " + waited.TotalSeconds.ToString("0") + " s. ";
        if (string.Equals(duration, "infinite", StringComparison.OrdinalIgnoreCase)) {
            return text + "The lease never ends on its own: it was taken by an older version of Relatude.DB, which could leave such "
                + "a lease behind when it stopped, or by another tool. Once no other instance is using this storage, break it in the admin "
                + "UI under Files, on this storage, with Leases.";
        }
        return text + "A lease left by a process that stopped without letting go ends within " + LeaseDurationSeconds + " s, so this one "
            + "belongs to a process that is still running: another instance of the application on the same storage, or the previous "
            + "process of a recycle that has not finished stopping. ";
    }
    /// <summary>Lets go of one stream's share of the lease, and of the lease itself with the last one.
    /// Never throws: a lease that could not be released ends by itself.</summary>
    internal void ReleaseLease(HeldLease held) {
        var removed = false;
        lock (leaseLockOf(held.BlobName)) {
            if (--held.Users > 0) return;
            if (_leases.TryGetValue(held.BlobName, out var current) && current == held) {
                _leases.TryRemove(held.BlobName, out _);
                removed = true;
            }
            if (held.LostReason == null) {
                try {
                    Client.ReleaseLease(held.BlobName, held.LeaseId);
                } catch (Exception err) {
                    log("Could not release the lease on \"" + held.BlobName + "\", it ends by itself within " + LeaseDurationSeconds + " s. " + err.Message);
                }
            }
        }
        if (removed) stopRenewTimerIfIdle();
    }
    /// <summary>
    /// After a write failed on the lease: renews it, which brings back a lease that expired as long as
    /// nobody wrote or leased the blob since - a process paused past the lease in a debugger, or
    /// renewals that could not get through for a while. False when the lease is gone for good.
    /// </summary>
    internal bool TryRecoverLease(HeldLease held) {
        lock (leaseLockOf(held.BlobName)) {
            if (held.LostReason != null) return false;
            try {
                Client.RenewLease(held.BlobName, held.LeaseId);
                return true;
            } catch (Exception err) {
                held.LostReason = err.Message;
                log("The lease on \"" + held.BlobName + "\" is lost: " + err.Message);
                return false;
            }
        }
    }
    void ensureRenewTimer() {
        lock (_renewTimerLock) {
            if (_renewTimer != null) return;
            var period = TimeSpan.FromSeconds(LeaseDurationSeconds / 3.0);
            _renewTimer = new Timer(_ => renewAll(), null, period, period);
        }
    }
    void stopRenewTimerIfIdle() {
        lock (_renewTimerLock) {
            if (_renewTimer == null || !_leases.IsEmpty) return;
            _renewTimer.Dispose();
            _renewTimer = null;
        }
    }
    void renewAll() {
        if (Interlocked.Exchange(ref _renewing, 1) == 1) return; // the last round is still going
        try {
            foreach (var held in _leases.Values) {
                lock (leaseLockOf(held.BlobName)) {
                    // let go of, or replaced, since the round started
                    if (held.LostReason != null || !_leases.TryGetValue(held.BlobName, out var current) || current != held) continue;
                    try {
                        Client.RenewLease(held.BlobName, held.LeaseId);
                    } catch (AzureBlobRequestException err) when (err.StatusCode is 404 or 409 or 412) {
                        // broken, taken by someone else after it expired, or the blob is gone
                        held.LostReason = err.Message;
                        log("The lease on \"" + held.BlobName + "\" is lost, writes to it will fail: " + err.Message);
                    } catch (Exception err) {
                        // a failure to get through: the lease lasts two more rounds, and a write finding it
                        // expired renews it again
                        log("Could not renew the lease on \"" + held.BlobName + "\", trying again in " + (LeaseDurationSeconds / 3) + " s. " + err.Message);
                    }
                }
            }
        } finally {
            Volatile.Write(ref _renewing, 0);
        }
    }

    /// <summary>
    /// Every blob in this container that someone holds a lease on, below <paramref name="folder"/> when
    /// one is given. The ones this provider holds are marked, as breaking those would only take a lock
    /// away from this process's own open files.
    /// </summary>
    public LeasedBlob[] GetLeasedFiles(string[]? folder = null) {
        var prefix = folder is { Length: > 0 } ? getAndValidateBlobName(folder) + _virtualFolderChar : null;
        return [.. Client.ListBlobs(prefix)
            .Where(b => b.LeaseState is "leased" or "breaking")
            .Select(b => new LeasedBlob {
                Key = b.Name,
                State = b.LeaseState!,
                Infinite = string.Equals(b.LeaseDuration, "infinite", StringComparison.OrdinalIgnoreCase),
                HeldHere = _leases.ContainsKey(b.Name),
            })];
    }
    /// <summary>
    /// Breaks the leases on the given blobs at once, whoever holds them - except the ones this provider
    /// holds itself, which are skipped. Breaking the lease of a process that is still running lets two
    /// processes write the same blobs, so this is for leases nobody is going to let go of: one that never
    /// ends, or one whose holder is known to be gone and that is not worth waiting out.
    /// </summary>
    public (int Broken, int Skipped, string[] Errors) BreakLeases(IEnumerable<string> keys) {
        int broken = 0, skipped = 0;
        var errors = new List<string>();
        foreach (var key in keys) {
            if (_leases.ContainsKey(key)) {
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
    // The count of readers or writers is taken first, under the lock, so a second open cannot slip past
    // the checks; the calls to the service come after, outside it, as taking a lease can wait. Whatever
    // fails on the way gives back what was taken by then - the count and the lease - or the file would
    // stay "locked" in this process, and leased for everyone else, until the process ends.
    IReadStream openRead(long position, string blobName) {
        FileMeta meta;
        lock (_lock) {
            if (!_files.TryGetValue(blobName, out meta!)) throw new Exception($"File {blobName} does not exist");
            if (meta.Writers > 0) throw new Exception($"File {blobName} is locked for writing. ");
            meta.Readers++;
        }
        HeldLease? lease = null;
        AzureBlobIOReadStream? stream = null;
        try {
            var properties = Client.GetProperties(blobName);
            if (_lockBlob && properties != null) lease = AcquireLease(blobName);
            stream = new AzureBlobIOReadStream(Client, blobName, position, properties, () => {
                try {
                    if (lease != null) ReleaseLease(lease);
                } finally {
                    lock (_lock) {
                        meta.Readers--;
                        _openStreams.Remove(stream!);
                    }
                }
            });
        } catch {
            if (lease != null) ReleaseLease(lease);
            lock (_lock) meta.Readers--;
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
        FileMeta meta;
        bool added;
        lock (_lock) {
            added = !_files.TryGetValue(fileKey, out meta!);
            if (added) {
                meta = new FileMeta { Key = fileKey };
                _files.Add(fileKey, meta);
            } else {
                if (meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
                if (meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
            }
            meta.Writers++;
        }
        HeldLease? lease = null;
        AzureBlobIOAppendStream? stream = null;
        try {
            Client.CreateAppendBlobIfNotExists(fileKey); // a lease needs a blob to be taken on
            if (_lockBlob) lease = AcquireLease(fileKey);
            stream = new AzureBlobIOAppendStream(this, Client, fileKey, lease, (long size) => {
                try {
                    if (lease != null) ReleaseLease(lease);
                } finally {
                    lock (_lock) {
                        meta.Writers--;
                        meta.LastModifiedUtc = DateTime.UtcNow;
                        meta.Size = size; // the folder listing reads this tracked meta, not the blob's properties
                        _openStreams.Remove(stream!);
                    }
                }
            });
        } catch {
            if (lease != null) ReleaseLease(lease);
            lock (_lock) {
                meta.Writers--;
                // a file this open made up is not one: the next listing finds it if the blob was created
                if (added && meta.Writers == 0 && meta.Readers == 0 && _files.TryGetValue(fileKey, out var m) && m == meta) _files.Remove(fileKey);
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
        FileMeta? meta;
        lock (_lock) {
            if (_files.TryGetValue(fileKey, out meta)) {
                if (meta.Readers > 0) throw new Exception($"File {fileKey} is locked for reading. ");
                if (meta.Writers > 0) throw new Exception($"File {fileKey} is locked for writing. ");
            }
        }
        try {
            Client.DeleteBlobIfExists(fileKey);
        } catch (AzureBlobRequestException err) when (err.ErrorCode == "LeaseIdMissing") {
            // someone holds a lease on it. One that never ends was left behind by an older version (see
            // the class) and nobody is going to let go of it, so it is broken, as that version did; any
            // other belongs to a process that is still using the file
            var duration = Client.GetProperties(fileKey)?.LeaseDuration;
            if (!string.Equals(duration, "infinite", StringComparison.OrdinalIgnoreCase)) {
                throw new FileLockedException("\"" + fileKey + "\" cannot be deleted: another process holds a lease on it. ", err);
            }
            Client.BreakLease(fileKey);
            Client.DeleteBlobIfExists(fileKey);
        }
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
    /// <summary>Closes every stream, each on its own: one that fails to flush still lets go of its lease,
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
        if (errors != null) throw new AggregateException("Not every stream closed cleanly. ", errors);
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
