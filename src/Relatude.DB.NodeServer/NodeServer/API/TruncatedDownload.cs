using Microsoft.AspNetCore.Http.Features;
using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.NodeServer.UI;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.NodeServer.API;

/// <summary>
/// The truncated database download: the store rewritten to its current state only, into a temp file,
/// and sent while it is being written rather than once it is done. The browser's download starts
/// with the first bytes - which also keeps a proxy in front (the 230 seconds an App Service waits for
/// a response to begin, say) from giving up on a large database - and the rewrite and the transfer
/// overlap, so the wait is the longer of the two rather than both.
/// <para>The rewrite holds its file with FileShare.None, so nothing can open it beside it: what is
/// written so far is read back through the rewrite's own append stream (see <see cref="TailIO"/>).
/// The rewrite only ever appends, so a byte once there is final. Once the rewrite lets go of the file
/// the rest is read from the file itself.</para>
/// <para>The admin UI follows it on its task board: the dialog that started it passes its task id,
/// and the rewrite's progress, then what is left to send, are what the task says. The temp file goes
/// once the rewrite is over and the response is done with it, sent in full or not.</para>
/// </summary>
static class TruncatedDownload {
    const int chunkBytes = 1024 * 1024;
    // how long to wait for more to be written before looking again
    const int waitMs = 200;

    public static async Task SendAsync(HttpContext ctx, RelatudeDBServer server, IDataStore datastore, string fileName, SharedTaskRef? task) {
        string[] fileKey = [Guid.NewGuid().ToString()];
        var temp = server.TempIO;
        var tail = new TailIO(temp, fileKey);
        var progress = new Progress();
        var rewrite = Task.Run(() => datastore.RewriteStore(false, fileKey, tail));
        server.UI?.Shared.Attach(task, () => progress.Describe(datastore, rewrite));
        var aborted = ctx.RequestAborted;
        try {
            var buffer = new byte[chunkBytes];
            // Nothing is sent before there is something to send: a rewrite refused at once - another
            // rewrite or copy running - is then still an error response rather than a cut-off download.
            while (!rewrite.IsCompleted && tail.Written == 0) {
                aborted.ThrowIfCancellationRequested();
                await Task.WhenAny(rewrite, Task.Delay(waitMs, aborted));
            }
            aborted.ThrowIfCancellationRequested();
            if (rewrite.IsFaulted) {
                var message = messageOf(rewrite);
                progress.End(UISharedTasks.Failed, message);
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await ctx.Response.WriteAsync(message, aborted);
                return;
            }
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            ctx.Response.ContentType = "application/octet-stream";
            var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(fileName);
            ctx.Response.Headers.ContentDisposition = disposition.ToString();
            await ctx.Response.StartAsync(aborted);
            var body = ctx.Response.Body;
            // while it is being written: what is there so far, through the rewrite's own stream
            while (!rewrite.IsCompleted) {
                aborted.ThrowIfCancellationRequested();
                var count = tail.TryRead(progress.Sent, buffer);
                if (count > 0) {
                    await body.WriteAsync(buffer.AsMemory(0, count), aborted);
                    progress.Add(count);
                } else {
                    await Task.WhenAny(rewrite, Task.Delay(waitMs, aborted));
                }
            }
            await rewrite; // a failed rewrite throws here: the response is cut off, and the browser says the download failed
            // the rest from the finished file, now that the rewrite has let go of it
            var total = temp.GetFileSizeOrZeroIfUnknown(fileKey);
            progress.Total = total;
            using (var rest = ReadStreamWrapper.Wrap(temp.OpenRead(fileKey, 0))) {
                rest.Position = progress.Sent;
                int read;
                while ((read = await rest.ReadAsync(buffer, aborted)) > 0) {
                    await body.WriteAsync(buffer.AsMemory(0, read), aborted);
                    progress.Add(read);
                }
            }
            await body.FlushAsync(aborted);
            progress.End(UISharedTasks.Done, "Downloaded " + total.ToByteString() + ".");
        } catch (Exception) when (aborted.IsCancellationRequested) {
            progress.End(UISharedTasks.Cancelled, "Stopped: the download was cancelled in the browser.");
        } catch (Exception error) {
            progress.End(UISharedTasks.Failed, rewrite.IsFaulted ? messageOf(rewrite) : error.Message);
            ctx.Abort(); // headers are out: cutting the response off is the only way left to say it failed
        } finally {
            // the copy is only ever for this download; a rewrite still running (the browser stopped
            // the download) writes on to its end, and the file goes then
            _ = rewrite.ContinueWith(_ => {
                try { temp.DeleteFileIfItExists(fileKey); } catch { }
            }, TaskScheduler.Default);
        }
    }

    static string messageOf(Task rewrite) => rewrite.Exception?.GetBaseException().Message ?? "The rewrite failed.";

    /// <summary>Where one download is, for its task on the board: written by the request, read by the board's polls.</summary>
    sealed class Progress {
        long _sent;
        long _total = -1;
        volatile SharedTaskProgress? _end;
        public long Sent => Interlocked.Read(ref _sent);
        public void Add(int count) => Interlocked.Add(ref _sent, count);
        /// <summary>The size of the finished file, known once the rewrite is done.</summary>
        public long Total { set => Interlocked.Exchange(ref _total, value); }
        public void End(string status, string message) => _end ??= new SharedTaskProgress(status, null, 0, null, null, message);

        public SharedTaskProgress Describe(IDataStore datastore, Task rewrite) {
            if (_end is { } end) return end;
            if (rewrite.IsFaulted) return new(UISharedTasks.Failed, null, 0, null, null, messageOf(rewrite));
            var sent = Sent;
            var total = Interlocked.Read(ref _total);
            if (total >= 0) return new(UISharedTasks.Running, "Sending the rest of the file…", sent, total, sent.ToByteString() + " of " + total.ToByteString(), null);
            var (step, percent) = rewriting(datastore);
            return new(UISharedTasks.Running, step is null ? "Rewriting the database file…" : "Rewriting: " + step, percent ?? 0, percent is null ? null : 100,
                (percent is null ? "" : percent + "% · ") + sent.ToByteString() + " sent so far", null);
        }

        // The rewrite's own account of where it is: the step it is on is the child of the activity
        // that names the file. A store runs one rewrite or copy at a time, so the one there is ours.
        static (string? Step, int? Percent) rewriting(IDataStore datastore) {
            try {
                var branch = datastore.GetStatus().ActivityTree.FirstOrDefault(b => b.Activity.Category == DataStoreActivityCategory.Rewriting);
                var step = branch?.Children.FirstOrDefault()?.Activity ?? branch?.Activity;
                return (step?.Description, step?.PercentageProgress);
            } catch {
                return (null, null);
            }
        }
    }

    /// <summary>
    /// The temp IO as the rewrite sees it, every call passed straight on, except that the append
    /// stream it opens for the one file being written is kept, so what it has written so far can be
    /// read back through it while it still holds the file.
    /// </summary>
    sealed class TailIO(IIOProvider inner, string[] fileKey) : IIOProvider {
        volatile IAppendStream? _writer;

        /// <summary>How many bytes the rewrite has written so far, while it holds the file.</summary>
        public long Written => _writer?.Length ?? 0;

        /// <summary>
        /// Reads what has been written from <paramref name="position"/> on, up to a buffer's worth, and
        /// says how much that was: none when nothing new is there yet, or when the rewrite has let go of
        /// the stream - the rest is then read from the file.
        /// </summary>
        public int TryRead(long position, byte[] buffer) {
            var writer = _writer;
            if (writer == null) return 0;
            try {
                var count = (int)Math.Min(buffer.Length, writer.Length - position);
                if (count <= 0) return 0;
                writer.Get(position, count, buffer);
                return count;
            } catch (ObjectDisposedException) {
                return 0;
            }
        }

        public IAppendStream OpenAppend(string[] path) {
            var stream = inner.OpenAppend(path);
            if (path.IsSameKey(fileKey)) _writer = stream;
            return stream;
        }
        public IReadStream OpenRead(string[] path, long position) => inner.OpenRead(path, position);
        public bool Exists(string[] path) => inner.Exists(path);
        public bool DoesNotExistOrIsEmpty(string[] path) => inner.DoesNotExistOrIsEmpty(path);
        public void DeleteFileIfItExists(string[] path) => inner.DeleteFileIfItExists(path);
        public FileMeta[] GetFiles() => inner.GetFiles();
        public long GetFileSizeOrZeroIfUnknown(string[] path) => inner.GetFileSizeOrZeroIfUnknown(path);
        public bool CanRenameFile => inner.CanRenameFile;
        public void RenameFile(string[] path, string[] newPath) => inner.RenameFile(path, newPath);
        public bool CanRenameFolder => inner.CanRenameFolder;
        public void RenameFolder(string[] path, string[] newPath) => inner.RenameFolder(path, newPath);
        public bool SupportsEmptyFolders => inner.SupportsEmptyFolders;
        public bool CanTruncate => inner.CanTruncate;
        public void TruncateFile(string[] path, long newLength) => inner.TruncateFile(path, newLength);
        public void CloseAllOpenStreams() => inner.CloseAllOpenStreams();
        public bool TryGetLocalFilePath(string[] path, [MaybeNullWhen(false)] out string localFilePath) => inner.TryGetLocalFilePath(path, out localFilePath);
        public bool TryGetLocalFolderPath(string[] path, [MaybeNullWhen(false)] out string localFolderPath) => inner.TryGetLocalFolderPath(path, out localFolderPath);
        public bool TryMoveIfSameDrive(string fromLocalFilePath, string[] destination) => inner.TryMoveIfSameDrive(fromLocalFilePath, destination);
        public void DeleteFolderIfItExists(string[] path) => inner.DeleteFolderIfItExists(path);
        public bool DeleteFolderIfEmpty(string[] path) => inner.DeleteFolderIfEmpty(path);
        public void EnsureFolder(string[] path) => inner.EnsureFolder(path);
        public Task<FolderMeta> GetFolderAsync(string[] path, bool recursive, bool withFiles) => inner.GetFolderAsync(path, recursive, withFiles);
    }
}
