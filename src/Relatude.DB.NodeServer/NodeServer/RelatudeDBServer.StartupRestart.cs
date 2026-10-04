using Relatude.DB.Common;
namespace Relatude.DB.NodeServer;

/// <summary>
/// The restart the server schedules for itself when the default database failed to open at startup
/// because another process still held its files, see <see cref="ServerOptions.StartupRestartAttempts"/>.
/// </summary>
public class ScheduledStartupRestart {
    /// <summary>When the restart runs.</summary>
    public DateTime DueUtc { get; set; }
    /// <summary>Which restart this is, counting from 1.</summary>
    public int Attempt { get; set; }
    /// <summary>How many the server makes before it gives up.</summary>
    public int Attempts { get; set; }
}

public partial class RelatudeDBServer {

    // The default database is watched from the start of the process until it has opened once: a lock
    // on its files makes the server restart itself a few minutes later, a few times.
    // 0 = watching its next auto-open, 1 = a restart is scheduled, 2 = done (it opened, it failed for
    // another reason, the attempts ran out, or the host is stopping)
    int _startupRestartState = 0;
    int _startupRestartsDone = 0;
    // set while StartAsync runs the auto-open itself: an open that holds the start up and fails takes
    // the start with it, so there is no process left for a restart to run in
    bool _startingUp = false;
    Timer? _startupRestartTimer;
    ScheduledStartupRestart? _scheduledStartupRestart;

    /// <summary>The restart the server has scheduled because the default database failed to open on a
    /// locked file, or null when none is.</summary>
    public ScheduledStartupRestart? ScheduledStartupRestart => Volatile.Read(ref _scheduledStartupRestart);

    int startupRestartAttempts => Math.Max(0, Options?.StartupRestartAttempts ?? ServerOptions.DefaultStartupRestartAttempts);

    /// <summary>Called once the first start has worked out what opens by itself: with no default
    /// database to open there is nothing to watch.</summary>
    void watchStartupOfDefault() {
        if (_defaultContainer == null || !_containersToAutoOpen.Contains(_defaultContainer) || startupRestartAttempts == 0) endStartupRestarts();
    }

    void onDefaultAutoOpened() {
        if (Interlocked.Exchange(ref _startupRestartState, 2) == 2) return;
        if (_startupRestartsDone > 0) logRestart("The default database opened after " + _startupRestartsDone + " restart(s) of the server.");
    }

    void onDefaultAutoOpenFailed(NodeStoreContainer container, Exception err, bool failsTheStart) {
        if (Volatile.Read(ref _startupRestartState) != 0) return; // done, or a restart is already on its way
        if (failsTheStart || IsShuttingDown || !isFileLock(err)) {
            endStartupRestarts(); // a restart cannot help, or there is nothing left to restart
            return;
        }
        var attempts = startupRestartAttempts;
        if (_startupRestartsDone >= attempts) {
            endStartupRestarts();
            logRestart("The default database \"" + container.Settings.Name + "\" still could not be opened after " + attempts
                + " restart(s) of the server. Giving up; open it from the admin UI once the other process has let go of its files.");
            return;
        }
        if (Interlocked.CompareExchange(ref _startupRestartState, 1, 0) != 0) return;
        var delay = Options?.StartupRestartDelay ?? ServerOptions.DefaultStartupRestartDelay;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        var scheduled = new ScheduledStartupRestart { DueUtc = DateTime.UtcNow + delay, Attempt = _startupRestartsDone + 1, Attempts = attempts };
        Volatile.Write(ref _scheduledStartupRestart, scheduled);
        logRestart("The default database \"" + container.Settings.Name + "\" could not be opened because another process holds one of its files."
            + " Restarting the server in " + describe(delay) + " to try again (" + scheduled.Attempt + " of " + attempts + ").");
        lock (_startupRestartLock) {
            _startupRestartTimer?.Dispose();
            _startupRestartTimer = new Timer(_ => _ = runStartupRestartAsync(), null, delay, Timeout.InfiniteTimeSpan);
        }
    }
    readonly object _startupRestartLock = new();

    async Task runStartupRestartAsync() {
        lock (_startupRestartLock) {
            _startupRestartTimer?.Dispose();
            _startupRestartTimer = null;
        }
        Volatile.Write(ref _scheduledStartupRestart, null);
        if (Volatile.Read(ref _startupRestartState) != 1) return; // cancelled while it waited
        if (IsShuttingDown) {
            endStartupRestarts();
            return;
        }
        var container = _defaultContainer;
        if (container != null && (container.IsOpen() || container.IsOpening)) {
            // opened by hand while it waited, or by a restart started from the admin UI
            endStartupRestarts();
            logRestart("Scheduled restart skipped, the default database is " + (container.IsOpen() ? "open" : "opening") + ".");
            return;
        }
        var attempt = Interlocked.Increment(ref _startupRestartsDone);
        Volatile.Write(ref _startupRestartState, 0); // the open this restart starts is watched like the first one
        try {
            var restarted = await softRestartAsync("Restarting the server, " + attempt + " of " + startupRestartAttempts
                + ", since the default database could not be opened on a locked file.");
            if (!restarted && !IsShuttingDown) logRestart("Another restart was already running, its open of the default database is watched instead.");
        } catch (Exception err) {
            // an open that holds requests until it is done throws out of the restart, and has already been
            // seen by onDefaultAutoOpenFailed; anything else ends the attempts here
            if (Volatile.Read(ref _startupRestartState) == 0) endStartupRestarts();
            logRestart("Scheduled restart failed: " + err.Message);
        }
    }

    /// <summary>Stops watching the default database and drops any restart scheduled for it.</summary>
    void endStartupRestarts() {
        Volatile.Write(ref _startupRestartState, 2);
        Volatile.Write(ref _scheduledStartupRestart, null);
        lock (_startupRestartLock) {
            _startupRestartTimer?.Dispose();
            _startupRestartTimer = null;
        }
    }

    // a lock may come wrapped: the index engines open in parallel, and the store wraps what fails its open
    static bool isFileLock(Exception? err) {
        if (err == null) return false;
        if (FileOpenRetry.IsSharingViolation(err)) return true;
        if (err is AggregateException aggregate) return aggregate.InnerExceptions.Any(isFileLock);
        return isFileLock(err.InnerException);
    }

    static string describe(TimeSpan delay) {
        if (delay.TotalSeconds < 1) return delay.TotalMilliseconds.To1000N() + " ms";
        if (delay.TotalMinutes < 2) return delay.TotalSeconds.To1000N() + " s";
        return delay.TotalMinutes.To1000N() + " minutes";
    }
}
