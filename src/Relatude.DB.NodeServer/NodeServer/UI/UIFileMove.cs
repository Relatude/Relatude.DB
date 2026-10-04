using Relatude.DB.IO;
using System.Buffers;
using System.Diagnostics;
namespace Relatude.DB.NodeServer.UI;
/// <summary>
/// Moving files and folders from one storage (IO provider) to another, for the Files view:
/// <code>
///   io-move-start      plans the move and starts it as a job, answers the job's id
///   io-move-progress   where it stands, and the byte counts sampled since the last poll
///   io-move-cancel     stops it: the files in flight are rolled back, the ones done stay moved
/// </code>
/// The bytes go from provider to provider on the server, never through the browser, so the job runs
/// here and the dialog only polls it. A big file then moves at the speed of the two storages rather
/// than of the link to whoever started it, its progress is counted inside the file rather than per
/// file, and Cancel is answered within one buffer.
/// <para>Every file is a move or nothing: it is copied - into the target's upload folder first when
/// the target can rename, so half a file never sits under the real name - the copy is checked for its
/// length and flushed, and only then is the original deleted. An original that cannot be deleted
/// (something holds it open) takes its copy away again rather than leave the file in both places.
/// A folder is removed once nothing is left in it.</para>
/// <para>The samples are what the dialog draws its speed from: the bytes moved and passed over so far,
/// read every quarter of a second on the server's own clock, so a poll that is late - a background
/// tab is only given a timer every second or so - leaves no gap in the picture.</para>
/// </summary>
sealed class UIFileMove {
    // files copied at once: enough to hide the round trip every file costs on blob storage or a
    // network share, few enough that big files on one disk do not fight over the heads
    const int parallelFiles = 4;
    const int copyBufferSize = 1024 * 1024;
    const int sampleIntervalMs = 250;
    // what a finished job keeps of its lists; the counts are always exact
    const int listedMax = 1000;
    static readonly Dictionary<Guid, MoveJob> _jobs = [];
    readonly RelatudeDBServer _server;
    internal UIFileMove(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("io-move-start", ctx => start(ctx.Payload<MoveStartPayload>()));
        commands.Register("io-move-progress", ctx => progress(ctx.Payload<MoveProgressPayload>()));
        commands.Register("io-move-cancel", ctx => {
            get(ctx.Payload<MoveProgressPayload>().JobId).Cancellation.Cancel();
            return (object?)new { Cancelled = true };
        });
    }

    static MoveJob get(Guid jobId) {
        lock (_jobs) {
            if (_jobs.TryGetValue(jobId, out var job)) return job;
        }
        throw new Exception("The move was not found. ");
    }

    /// <summary>
    /// Checks what can be checked without listing anything - the storages, the target folder's name,
    /// and that the target is not inside a folder being moved - so a move that cannot work fails on
    /// the button rather than in the dialog, then starts the job.
    /// </summary>
    object start(MoveStartPayload p) {
        if (p.FromIoId == p.ToIoId) throw new Exception("The files are already in that storage. ");
        var from = _server.GetIO(p.FromIoId);
        var to = _server.GetIO(p.ToIoId);
        var target = split(p.TargetPath);
        var badSegment = target.FirstOrDefault(segment => !UIServer.IsValidSegment(to, segment));
        if (badSegment != null) throw new Exception($"\"{badSegment}\" is not a folder name that storage accepts. ");
        // a ticked folder inside another ticked folder goes with its parent, and so does a file
        var folders = (p.Folders ?? []).Select(split).Where(f => f.Length > 0).Distinct(KeyComparer.Instance).ToList();
        folders = [.. folders.Where(f => !folders.Any(other => other.Length < f.Length && isBelow(f, other)))];
        var files = (p.Files ?? []).Select(split).Where(f => f.Length > 0).Distinct(KeyComparer.Instance)
            .Where(f => !folders.Any(folder => isBelow(f, folder))).ToList();
        if (files.Count + folders.Count == 0) throw new Exception("Nothing to move. ");
        // Two storages can be the same folder on disk seen twice - the website project folder holds
        // the database's own folder, usually. A folder moved into itself would be deleted with its
        // copy inside it once the move is done.
        if (to.TryGetLocalFolderPath(target, out var targetLocal)) {
            foreach (var folder in folders) {
                if (from.TryGetLocalFolderPath(folder, out var folderLocal) && isSameOrBelow(targetLocal, folderLocal)) {
                    throw new Exception($"The target folder is inside {folder.AsKeyString()}, which is being moved. ");
                }
            }
        }
        var job = new MoveJob(p with { TargetPath = target.AsKeyString() });
        lock (_jobs) {
            foreach (var old in _jobs.Values.Where(j => j.Finished && DateTime.UtcNow - j.StartedUtc > TimeSpan.FromHours(1)).ToArray()) _jobs.Remove(old.Id);
            _jobs[job.Id] = job;
        }
        _ = Task.Run(() => runAsync(job, from, to, split(p.BasePath), target, files, folders));
        return new { JobId = job.Id };
    }

    object progress(MoveProgressPayload p) {
        var job = get(p.JobId);
        var finished = job.Finished; // read once: the lists below are only sent with the answer that says so
        return new {
            State = job.State,
            job.Error,
            job.Current,
            job.FilesTotal,
            job.FilesMoved,
            job.FilesSkipped,
            job.FilesFailed,
            job.FoldersRemoved,
            job.BytesTotal,
            BytesMoved = job.BytesSettled,
            BytesTransferred = Interlocked.Read(ref job.BytesTransferred),
            BytesPassed = Interlocked.Read(ref job.BytesPassed),
            ElapsedMs = job.Clock.ElapsedMilliseconds,
            Samples = job.SamplesFrom(Math.Max(0, p.SamplesFrom)),
            Errors = finished ? job.Listed(job.Errors) : null,
            Skipped = finished ? job.Listed(job.Skipped) : null,
        };
    }

    async Task runAsync(MoveJob job, IIOProvider from, IIOProvider to, string[] basePath, string[] target, List<string[]> files, List<string[]> folders) {
        var token = job.Cancellation.Token;
        try {
            var plan = await planAsync(job, from, to, basePath, target, files, folders, token);
            job.FilesTotal = plan.Files.Count + job.FilesFailed; // a selected file that was not found counts as one that failed
            job.BytesTotal = plan.Files.Sum(f => f.Size);
            job.State = MoveJob.Moving;
            // an empty folder has nothing to carry it across where folders are real, so it is made
            if (to.SupportsEmptyFolders) {
                foreach (var folder in plan.TargetFolders) {
                    try { to.EnsureFolder(folder); } catch { } // a name that storage refuses: its files will say so
                }
            }
            // Between two folders of one disk a move is a rename, and renames on one volume queue for
            // its directories: four at once took seven times as long as one after the other (300
            // files, NTFS with a virus scanner). Copies are the other way round - the waits are on
            // the bytes and the round trips, and those overlap.
            var parallel = renames(job, from, to) ? 1 : parallelFiles;
            await Parallel.ForEachAsync(plan.Files, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = token },
                (file, ct) => new ValueTask(moveOneAsync(job, from, to, file, ct)));
            // A folder goes once nothing is left in it - a file that was skipped, failed or written
            // meanwhile keeps it. Not after a cancel: what is left is then simply what was not done.
            if (!job.Request.KeepOriginals) {
                job.State = MoveJob.Tidying;
                foreach (var folder in folders) {
                    token.ThrowIfCancellationRequested();
                    job.Current = folder.AsKeyString();
                    try {
                        if (countFiles(await from.GetFolderAsync(folder, true, true)) > 0) continue;
                        from.DeleteFolderIfItExists(folder);
                        Interlocked.Increment(ref job.FoldersRemoved);
                    } catch (Exception e) {
                        job.AddLine(job.Errors, folder.AsKeyString() + "/: the folder was emptied but could not be removed (" + e.Message.Trim() + ")");
                    }
                }
            }
            job.Current = "";
            job.Finish(MoveJob.Done, null);
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            job.Finish(MoveJob.Cancelled, null);
        } catch (Exception e) {
            job.Finish(MoveJob.Failed, e.Message);
        }
    }

    sealed record PlannedFile(string[] Source, string[] Target, long Size, int Readers, int Writers);
    sealed record Plan(List<PlannedFile> Files, List<string[]> TargetFolders);

    /// <summary>
    /// What goes where. A selected file keeps the path it has below the open folder (it may come from
    /// a listing that reaches into the subfolders), a folder arrives under its own name in the target
    /// with everything below it. Sizes and the open stream counts come from the listings, which is
    /// what a file in use is told by without opening it - opening a held file waits for it.
    /// </summary>
    static async Task<Plan> planAsync(MoveJob job, IIOProvider from, IIOProvider to, string[] basePath, string[] target,
        List<string[]> files, List<string[]> folders, CancellationToken token) {
        var planned = new List<PlannedFile>();
        var targetFolders = new List<string[]>();
        foreach (var folder in folders) {
            token.ThrowIfCancellationRequested();
            job.Current = folder.AsKeyString();
            var meta = await from.GetFolderAsync(folder, true, true);
            string[] into = [.. target, folder[^1]];
            void walk(FolderMeta f, string[] targetFolder) {
                targetFolders.Add(targetFolder);
                foreach (var file in f.Files) {
                    var key = file.KeyOf();
                    planned.Add(new PlannedFile(key, [.. targetFolder, key[^1]], file.Size, file.Readers, file.Writers));
                }
                foreach (var sub in f.SubFolders) walk(sub, [.. targetFolder, sub.Name]);
            }
            walk(meta, into);
            job.Current = folder.AsKeyString() + " (" + planned.Count + " files)";
        }
        foreach (var group in files.GroupBy(f => f[..^1].AsKeyString(), StringComparer.OrdinalIgnoreCase)) {
            token.ThrowIfCancellationRequested();
            var meta = await from.GetFolderAsync(group.Key.SplitKey(), false, true);
            var byKey = new Dictionary<string, FileMeta>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in meta.Files) byKey[file.Key] = file;
            foreach (var key in group) {
                var relative = isBelow(key, basePath) ? key[basePath.Length..] : [key[^1]];
                if (byKey.TryGetValue(key.AsKeyString(), out var file)) {
                    planned.Add(new PlannedFile(key, [.. target, .. relative], file.Size, file.Readers, file.Writers));
                } else {
                    job.AddLine(job.Errors, key.AsKeyString() + ": the file was not found");
                    Interlocked.Increment(ref job.FilesFailed);
                }
            }
        }
        return new Plan(planned, targetFolders);
    }

    /// <summary>
    /// One file across. Whatever goes wrong with it is said about it and the move carries on with the
    /// next one; only a cancel ends the move from here.
    /// <para>The bytes that went across stay counted even when the file then fails: they did go, and
    /// the speed is about what the storages did. What the file did not get to is counted as passed,
    /// so the two together still come to its size and the bar ends at the end.</para>
    /// </summary>
    async Task moveOneAsync(MoveJob job, IIOProvider from, IIOProvider to, PlannedFile file, CancellationToken token) {
        var name = file.Source.AsKeyString();
        job.Current = name;
        var copied = new CopyCount();
        void pass() => Interlocked.Add(ref job.BytesPassed, Math.Max(0, file.Size - copied.Bytes));
        void fail(string reason) {
            job.AddLine(job.Errors, name + ": " + reason);
            Interlocked.Increment(ref job.FilesFailed);
            pass();
        }
        try {
            if (file.Writers > 0) {
                fail("it is being written to, so a copy of it would not be the whole file");
                return;
            }
            if (file.Target.Any(segment => !UIServer.IsValidSegment(to, segment)) || FileKeyUtility.State_IsStateFileKey(file.Target)) {
                fail("the name is not allowed in that storage");
                return;
            }
            // the same file seen through two storages: copying it onto itself would destroy it
            if (from.TryGetLocalFilePath(file.Source, out var sourceLocal) && to.TryGetLocalFolderPath(file.Target, out var targetLocal)
                && string.Equals(Path.GetFullPath(sourceLocal), Path.GetFullPath(targetLocal), StringComparison.OrdinalIgnoreCase)) {
                fail("it is the same file in both storages");
                return;
            }
            var replacing = to.Exists(file.Target);
            if (replacing && !job.Request.Overwrite) {
                job.AddLine(job.Skipped, name);
                Interlocked.Increment(ref job.FilesSkipped);
                pass();
                return;
            }
            var keep = job.Request.KeepOriginals;
            // Two folders on one disk: the file is renamed across, which takes no time whatever its
            // size. Only for a file nothing has open - a rename would pull it from under a reader.
            if (!keep && sourceLocal != null && file.Readers == 0 && to is IOProviderDisk) {
                if (replacing) to.DeleteFileIfItExists(file.Target);
                if (to.TryMoveIfSameDrive(sourceLocal, file.Target)) {
                    Interlocked.Add(ref job.BytesTransferred, file.Size);
                    job.Settle(file.Size);
                    return;
                }
            }
            await copyAsync(job, from, to, file, copied, token);
            if (!keep) {
                try {
                    from.DeleteFileIfItExists(file.Source);
                } catch (Exception e) {
                    // a move or nothing: the copy goes again rather than leave the file in both places
                    try { to.DeleteFileIfItExists(file.Target); } catch { }
                    fail("the original could not be removed (" + e.Message.Trim() + "), so its copy was taken away again");
                    return;
                }
            }
            job.Settle(copied.Bytes);
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            pass();
            throw;
        } catch (Exception e) {
            fail(e is IOException ? "it is in use (" + e.Message.Trim() + ")" : e.Message.Trim());
        }
    }

    // how far the copy of one file got, readable after it has thrown
    sealed class CopyCount {
        public long Bytes;
    }

    /// <summary>
    /// Copies the file, counting every buffer into the job as it goes. The copy is written to a
    /// temp key and renamed into place where the target can rename; where it cannot (blob storage)
    /// it is written under its name and taken away again if anything goes wrong on the way.
    /// </summary>
    static async Task copyAsync(MoveJob job, IIOProvider from, IIOProvider to, PlannedFile file, CopyCount copied, CancellationToken token) {
        // not shared with writers: a file being written is not copied (see moveOneAsync), and one
        // that starts being written mid copy fails here rather than arrive half written
        using var source = UIServer.OpenFileForReading(from, file.Source, shareWithWriters: false) ?? throw new Exception("the file was not found");
        var stage = to.CanRenameFile ? UIFileTransfer.UploadTempKey(Guid.NewGuid()) : file.Target;
        if (!to.CanRenameFile) to.DeleteFileIfItExists(file.Target);
        var buffer = ArrayPool<byte>.Shared.Rent(copyBufferSize);
        try {
            using (var target = to.OpenAppend(stage)) {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, copyBufferSize), token)) > 0) {
                    target.Append(buffer, read);
                    copied.Bytes += read;
                    Interlocked.Add(ref job.BytesTransferred, read);
                    token.ThrowIfCancellationRequested();
                }
                // on the disk before the original is deleted: a crash then must not cost both
                target.Flush(true);
            }
            var written = to.GetFileSizeOrZeroIfUnknown(stage);
            if (written != copied.Bytes || copied.Bytes != source.Length) {
                throw new Exception($"the copy came out at {written} bytes, but {source.Length} were read");
            }
            if (!stage.IsSameKey(file.Target)) UIFileTransfer.MoveIntoPlace(to, stage, file.Target);
        } catch {
            try { to.DeleteFileIfItExists(stage); } catch { }
            throw;
        } finally {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // whether the files will be renamed across rather than copied (see moveOneAsync)
    static bool renames(MoveJob job, IIOProvider from, IIOProvider to) =>
        !job.Request.KeepOriginals && from is IOProviderDisk fromDisk && to is IOProviderDisk toDisk
        && string.Equals(Path.GetPathRoot(Path.GetFullPath(fromDisk.BaseFolder)), Path.GetPathRoot(Path.GetFullPath(toDisk.BaseFolder)), StringComparison.OrdinalIgnoreCase);

    static int countFiles(FolderMeta folder) => folder.Files.Length + folder.SubFolders.Sum(countFiles);
    static string[] split(string? path) => path?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
    // whether key lies below folder (and is not the folder itself)
    static bool isBelow(string[] key, string[] folder) =>
        key.Length > folder.Length && key.AsSpan(0, folder.Length).SequenceEqual(folder, StringComparer.OrdinalIgnoreCase);
    static bool isSameOrBelow(string path, string folder) {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    sealed class KeyComparer : IEqualityComparer<string[]> {
        public static readonly KeyComparer Instance = new();
        public bool Equals(string[]? x, string[]? y) => x.IsSameKey(y);
        public int GetHashCode(string[] key) => StringComparer.OrdinalIgnoreCase.GetHashCode(key.AsKeyString());
    }

    /// <summary>One move: the counters the copies write and the polls read, and the sampler.</summary>
    sealed class MoveJob {
        public const string Listing = "listing";
        public const string Moving = "moving";
        public const string Tidying = "tidying";
        public const string Done = "done";
        public const string Cancelled = "cancelled";
        public const string Failed = "failed";
        public Guid Id { get; } = Guid.NewGuid();
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public CancellationTokenSource Cancellation { get; } = new();
        public MoveStartPayload Request { get; }
        // written by the copies, read by the polls; a torn read of a label is harmless
        public volatile string State = Listing;
        public volatile string? Error;
        public volatile string Current = "";
        public volatile int FilesTotal;
        public long BytesTotal;
        public int FilesMoved, FilesSkipped, FilesFailed, FoldersRemoved;
        // BytesTransferred is what went across, counted buffer by buffer (and a renamed file whole):
        // the speed. BytesPassed is what was decided without moving it - skipped, failed. Together
        // they are how far through BytesTotal the move is.
        public long BytesTransferred, BytesPassed;
        long _bytesSettled; // the sizes of the files that are done: what "moved" says at the end
        public long BytesSettled => Interlocked.Read(ref _bytesSettled);
        public readonly List<string> Errors = [];
        public readonly List<string> Skipped = [];
        int _errorCount, _skippedCount;
        readonly List<long[]> _samples = [];
        readonly Timer _sampler;
        public bool Finished => State is Done or Cancelled or Failed;

        public MoveJob(MoveStartPayload request) {
            Request = request;
            _samples.Add([0, 0, 0]);
            _sampler = new Timer(_ => sample(), null, sampleIntervalMs, sampleIntervalMs);
        }
        void sample() {
            lock (_samples) _samples.Add([Clock.ElapsedMilliseconds, Interlocked.Read(ref BytesTransferred), Interlocked.Read(ref BytesPassed)]);
        }
        /// <summary>The samples from the given index on: [milliseconds since start, bytes transferred, bytes passed].</summary>
        public long[][] SamplesFrom(int index) {
            lock (_samples) return index >= _samples.Count ? [] : [.. _samples.Skip(index)];
        }
        public void Settle(long size) {
            Interlocked.Increment(ref FilesMoved);
            Interlocked.Add(ref _bytesSettled, size);
        }
        public void AddLine(List<string> list, string line) {
            lock (list) {
                var count = list == Errors ? ++_errorCount : ++_skippedCount;
                if (count <= listedMax) list.Add(line);
            }
        }
        /// <summary>The list as the dialog shows it, with a last line for what did not fit.</summary>
        public List<string> Listed(List<string> list) {
            lock (list) {
                var count = list == Errors ? _errorCount : _skippedCount;
                var shown = new List<string>(list);
                if (count > shown.Count) shown.Add($"… and {count - shown.Count} more");
                return shown;
            }
        }
        public void Finish(string state, string? error) {
            Clock.Stop(); // ElapsedMs is then how long the move took, however late it is asked for
            _sampler.Dispose();
            sample(); // the last one, so the picture ends where the move did
            Error = error;
            Current = "";
            State = state;
        }
    }
}
/// <summary>
/// Files are keys of the source storage; BasePath is the folder the Files view had open, which a file
/// keeps its path below (a listing can reach into subfolders). Folders arrive in TargetPath under
/// their own name. Overwrite replaces a file the target already has, otherwise it is skipped and stays
/// where it was. KeepOriginals makes it a copy.
/// </summary>
sealed record MoveStartPayload(Guid FromIoId, Guid ToIoId, string? BasePath, string[]? Files, string[]? Folders, string? TargetPath,
    bool Overwrite = false, bool KeepOriginals = false);
/// <summary>SamplesFrom is how many samples the client already has.</summary>
sealed record MoveProgressPayload(Guid JobId, int SamplesFrom = 0);
