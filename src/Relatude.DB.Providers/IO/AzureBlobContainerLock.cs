using Relatude.DB.Common;
namespace Relatude.DB.IO;

/// <summary>
/// The lease on a container's lock blob, which keeps every other process from writing the container. Taken by
/// the first write of this process and held until the streams are closed with no write going on - a database
/// closing - so later writes cost nothing; one per container in a process, shared by every provider on it. The
/// lease runs for 15 s and is renewed every 5 s on a thread of its own, so the lock of a process that was killed
/// ends within 15 s, and a write in another process waits 20 s for it.
/// </summary>
internal sealed class AzureBlobContainerLock {
    internal const string BlobName = "_lock";
    internal const int LeaseSeconds = 15;
    internal static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(20);
    // writes stop this long before a lease that was not renewed could run out
    const long _marginMs = 2000;
    static void log(string message) => FileOpenRetry.DefaultLog?.Invoke(message);

    readonly object _sync = new();
    Lease? _current;

    public bool HeldHere => _current is { LostReason: null };

    /// <exception cref="BlobLeasedException">Another process still held the lock after <paramref name="wait"/>.</exception>
    public Lease Enter(AzureBlobRestClient client, TimeSpan wait) {
        lock (_sync) {
            // a lost lease stays with the writes that began under it; a new one takes the lock again
            if (_current == null || _current.LostReason != null) _current = Lease.Acquire(client, wait);
            _current.Users++;
            return _current;
        }
    }
    public void Exit(Lease lease) {
        lock (_sync) lease.Users--;
    }
    public void ReleaseIfIdle() {
        lock (_sync) {
            if (_current == null || _current.Users > 0) return;
            _current.Release();
            _current = null;
        }
    }

    internal sealed class Lease {
        readonly AzureBlobRestClient _client;
        readonly string _id;
        readonly ManualResetEventSlim _released = new();
        long _writableUntil; // Environment.TickCount64
        public int Users;
        public volatile string? LostReason;

        Lease(AzureBlobRestClient client, string id, long sentAt) {
            _client = client;
            _id = id;
            _writableUntil = sentAt + LeaseSeconds * 1000 - _marginMs;
            // not the thread pool: a pool starved under load would let the lease run out
            new Thread(renewUntilReleased) { IsBackground = true, Name = "Blob container lock" }.Start();
        }
        public static Lease Acquire(AzureBlobRestClient client, TimeSpan wait) {
            client.CreateAppendBlobIfNotExists(BlobName);
            var proposedId = Guid.NewGuid().ToString(); // a retried acquire that already went through gets the lease back
            long sentAt = 0;
            var id = Retry.Run(() => {
                sentAt = Environment.TickCount64;
                return client.AcquireLease(BlobName, LeaseSeconds, proposedId);
            }, isLeasedElsewhere, wait,
                onWaitStarted: (_, _) => log("\"" + client.ContainerUrl + "\" is locked by another process, waiting up to "
                    + wait.TotalSeconds.ToString("0") + " s for it to let go, or for its lock to end. "),
                onWaitEnded: (attempts, elapsed) => log("\"" + client.ContainerUrl + "\" was let go of after "
                    + elapsed.TotalSeconds.ToString("0.0") + " s, locked on attempt " + attempts + "."),
                onExhausted: (err, _, elapsed) => new BlobLeasedException("\"" + client.ContainerUrl + "\" is locked by another process, which still held it after "
                    + elapsed.TotalSeconds.ToString("0") + " s. A process that stopped without letting go loses the lock within " + LeaseSeconds
                    + " s, so this one is still running: another instance of the application on the same storage, or the previous process of a "
                    + "recycle that has not finished stopping. ", err, BlobName, false));
            return new Lease(client, id, sentAt);
        }
        // LeaseAlreadyPresent, or LeaseIsBreakingAndCannotBeAcquired while a break with a period runs out
        static bool isLeasedElsewhere(Exception err) => err is AzureBlobRequestException { StatusCode: 409 } e
            && e.ErrorCode != null && e.ErrorCode.StartsWith("Lease", StringComparison.Ordinal);

        /// <summary>Throws unless the lock is surely this process's: not lost, and renewed recently enough
        /// that it cannot have run out and been taken by another process.</summary>
        public void EnsureWritable(string blobName) {
            if (LostReason is string reason) throw new IOException("\"" + blobName + "\" is not written: the lock on \"" + _client.ContainerUrl
                + "\" is lost (" + reason.TrimEnd() + "), so another process may be writing it now. The database has to be opened again. ");
            if (Environment.TickCount64 > Volatile.Read(ref _writableUntil)) throw new IOException("\"" + blobName + "\" is not written: the lock on \""
                + _client.ContainerUrl + "\" could not be renewed for " + (LeaseSeconds * 1000 - _marginMs) / 1000 + " s, so it may have run out. ");
        }
        void renewUntilReleased() {
            while (!_released.Wait(RenewEvery)) {
                var sentAt = Environment.TickCount64;
                try {
                    _client.RenewLease(BlobName, _id);
                    Volatile.Write(ref _writableUntil, sentAt + LeaseSeconds * 1000 - _marginMs);
                } catch (AzureBlobRequestException err) when (err.StatusCode is 404 or 409 or 412) {
                    if (_released.IsSet) return;
                    LostReason = err.Message;
                    log("The lock on \"" + _client.ContainerUrl + "\" is lost, nothing more is written to it: " + err.Message);
                    return;
                } catch (Exception err) {
                    // renewing an expired lease still works as long as nobody else took it meanwhile
                    log("Could not renew the lock on \"" + _client.ContainerUrl + "\", trying again in " + RenewEvery.TotalSeconds.ToString("0") + " s. " + err.Message);
                }
            }
        }
        public void Release() {
            _released.Set();
            if (LostReason != null) return;
            try {
                _client.ReleaseLease(BlobName, _id);
            } catch (Exception err) {
                log("Could not let go of the lock on \"" + _client.ContainerUrl + "\", it ends by itself within " + LeaseSeconds + " s. " + err.Message);
            }
        }
    }
}
