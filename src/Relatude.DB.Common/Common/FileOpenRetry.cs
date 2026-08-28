using System.Diagnostics;
namespace Relatude.DB.Common;

/// <summary>
/// Thrown when a file was still held by another process after the whole retry budget was spent.
/// A distinct type so callers can tell "someone else has it" apart from "the contents are broken" -
/// the two need opposite responses, and confusing them costs a needless index rebuild.
/// </summary>
public class FileLockedException(string message, Exception? innerException)
    : IOException(message, innerException) {
}

/// <summary>
/// Opens files that another process may still be holding.
/// <para>This exists because of how hosts hand an application over. Azure App Service recycles with
/// the workers overlapping - the new process starts before the old one has finished stopping - so the
/// new process reaches its database open while the previous one still owns the log and the index
/// files. Container platforms and IIS do the same thing during a deploy, and a backup agent can hold
/// a file for a moment at any time. The lock always clears within seconds; the only wrong answer is
/// to give up on the first attempt.</para>
/// <para>Only a sharing violation is retried. Everything else - a missing file, a bad path, a corrupt
/// header - fails immediately, because waiting cannot help and a swallowed error is worse than a slow
/// one.</para>
/// </summary>
public static class FileOpenRetry {

    /// <summary>Long enough to cover a host handover, short enough not to look like a hang.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Where a wait is reported when the caller passes no logger of its own. Most of the callers are
    /// deep in the storage engines, with no logger in reach, and a wait nobody can see is
    /// indistinguishable from a hang - so this defaults to standard error, which is where a host's log
    /// stream picks it up (the Azure App Service log stream included). Set it to redirect, or to null
    /// to silence it.
    /// </summary>
    public static Action<string>? DefaultLog { get; set; } = msg => Console.Error.WriteLine("relatude.db: " + msg);

    /// <summary>
    /// Whether this exception - or anything it wraps - means another process is holding the file,
    /// rather than the file being missing or unusable.
    /// <para>Windows reports a held file as <c>ERROR_SHARING_VIOLATION</c> (32) or
    /// <c>ERROR_LOCK_VIOLATION</c> (33) in the HResult. The message check behind it covers the hosts
    /// that surface the same condition without those codes, notably SMB shares such as the Azure
    /// Files mount behind <c>/home</c> on App Service.</para>
    /// <para><see cref="UnauthorizedAccessException"/> counts, which is a deliberate trade: an SMB
    /// share can report a locked file that way, and the cost of being wrong is that a genuine
    /// permission problem takes the retry budget to surface instead of failing at once.</para>
    /// </summary>
    public static bool IsSharingViolation(Exception? err) {
        while (err != null) {
            switch (err) {
                case FileLockedException:
                    return true;
                case FileNotFoundException:
                case DirectoryNotFoundException:
                    return false; // an IOException, but retrying will not conjure the file
                case UnauthorizedAccessException:
                    return true;
                case IOException io:
                    var code = io.HResult & 0xFFFF;
                    if (code == 32 || code == 33) return true;
                    if (io.Message.Contains("another process", StringComparison.OrdinalIgnoreCase)
                        || io.Message.Contains("being used by", StringComparison.OrdinalIgnoreCase)
                        || io.Message.Contains("text file busy", StringComparison.OrdinalIgnoreCase)) return true;
                    break;
            }
            err = err.InnerException;
        }
        return false;
    }

    /// <summary>
    /// Runs <paramref name="open"/>, and while it fails because another process holds the file, waits
    /// and runs it again until <paramref name="timeout"/> is spent. Any other failure is rethrown
    /// untouched, on the attempt it happened. The waiting rhythm is <see cref="Retry"/>'s, shared with
    /// every other retry in the database.
    /// <para><paramref name="log"/> - or <see cref="DefaultLog"/> when it is null - is called at most
    /// twice: once when the wait begins, once when it ends. A wait that is never logged is
    /// indistinguishable from a hang.</para>
    /// </summary>
    /// <exception cref="FileLockedException">The file was still held when the budget ran out.</exception>
    public static T Open<T>(string path, Func<T> open, TimeSpan? timeout = null, Action<string>? log = null) {
        log ??= DefaultLog;
        var budget = timeout ?? DefaultTimeout;
        var name = Path.GetFileName(path);
        return Retry.Run(open, IsSharingViolation, budget,
            onWaitStarted: (_, err) => log?.Invoke("\"" + name + "\" is held by another process, waiting up to "
                + budget.TotalSeconds.ToString("0") + " s for it to be released. " + err.Message),
            onWaitEnded: (attempts, elapsed) => log?.Invoke("\"" + name + "\" was released by the other process after "
                + elapsed.TotalSeconds.ToString("0.0") + " s, opened on attempt " + attempts + "."),
            onExhausted: (err, attempts, elapsed) => new FileLockedException(
                "\"" + path + "\" is held by another process and was still held after "
                + elapsed.TotalSeconds.ToString("0.0") + " s over " + attempts + " attempt(s). "
                + "On a host that recycles with the processes overlapping, such as Azure App Service, the previous "
                + "process may not have finished stopping. " + err.Message, err));
    }

    /// <summary>Same as <see cref="Open{T}"/>, for an open that returns nothing.</summary>
    public static void Open(string path, Action open, TimeSpan? timeout = null, Action<string>? log = null) {
        Open(path, () => { open(); return true; }, timeout, log);
    }
}
