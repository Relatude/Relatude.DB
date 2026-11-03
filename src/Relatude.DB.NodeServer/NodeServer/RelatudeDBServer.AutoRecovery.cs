using System.Diagnostics;
using System.Globalization;
using System.Text;
using Relatude.DB.Common;
using Relatude.DB.IO;
namespace Relatude.DB.NodeServer;

public enum AutoRecoveryStep {
    RestartDatabase,
    RestartServer,
    RestartProcess,
}

public class ScheduledRecovery {
    public DateTime DueUtc { get; set; }
    public AutoRecoveryStep Step { get; set; }
    public int Incident { get; set; }
    public int Incidents { get; set; }
}

public partial class RelatudeDBServer {

    sealed class RecoveryIncident(int number, DateTime startedUtc) {
        public int Number => number;
        public DateTime StartedUtc => startedUtc;
        public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
        public HashSet<Guid> DatabaseIds { get; } = [];
        public AutoRecoveryStep NextStep { get; set; } = AutoRecoveryStep.RestartDatabase;
        public DateTime NextStepDueUtc { get; set; }
        public int StepsTaken { get; set; }
    }

    static readonly TimeSpan recoveryWindow = TimeSpan.FromHours(24);
    bool _startingUp = false;
    readonly object _recoveryLock = new();
    readonly List<DateTime> _recoveryIncidentsUtc = [];
    RecoveryIncident? _recoveryIncident;
    Timer? _recoveryTimer;

    TimeSpan recoveryDelay {
        get {
            var delay = Options?.AutoRecoveryDelay ?? ServerOptions.DefaultAutoRecoveryDelay;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }
    }
    int recoveryIncidentsPerDay => Math.Max(0, Options?.AutoRecoveryIncidentsPerDay ?? ServerOptions.DefaultAutoRecoveryIncidentsPerDay);

    public bool IsRecovering {
        get { lock (_recoveryLock) return _recoveryIncident != null; }
    }

    public ScheduledRecovery? GetScheduledRecovery(Guid databaseId) {
        lock (_recoveryLock) {
            var incident = _recoveryIncident;
            if (incident == null || _recoveryTimer == null || !incident.DatabaseIds.Contains(databaseId)) return null;
            return new ScheduledRecovery {
                DueUtc = incident.NextStepDueUtc,
                Step = incident.NextStep,
                Incident = incident.Number,
                Incidents = recoveryIncidentsPerDay,
            };
        }
    }

    internal void OnCriticalErrorWhileOpen(NodeStoreContainer container, string description, Exception error)
        => requestRecovery(container, "The database went into a critical error state. " + description.Trim() + " " + error.Message);

    void onAutoOpenFailed(NodeStoreContainer container, Exception error, bool failsTheStart) {
        if (failsTheStart) return;
        requestRecovery(container, "The database failed to open on start-up. " + error.Message);
    }

    void requestRecovery(NodeStoreContainer container, string trigger) {
        var budget = recoveryIncidentsPerDay;
        if (!Settings.AutoRecovery || budget == 0 || IsShuttingDown) return;
        var id = container.Settings.Id;
        var now = DateTime.UtcNow;
        RecoveryIncident? started = null;
        RecoveryIncident? joined = null;
        DateTime? oldestInWindow = null;
        lock (_recoveryLock) {
            if (_recoveryIncident != null) {
                if (!_recoveryIncident.DatabaseIds.Add(id)) return;
                joined = _recoveryIncident;
            } else {
                _recoveryIncidentsUtc.RemoveAll(t => now - t >= recoveryWindow);
                if (_recoveryIncidentsUtc.Count >= budget) {
                    oldestInWindow = _recoveryIncidentsUtc.Count == 0 ? null : _recoveryIncidentsUtc.Min();
                } else {
                    started = new RecoveryIncident(_recoveryIncidentsUtc.Count + 1, now);
                    started.DatabaseIds.Add(id);
                    _recoveryIncidentsUtc.Add(now);
                    _recoveryIncident = started;
                    scheduleRecoveryStep(started, AutoRecoveryStep.RestartDatabase);
                }
            }
        }
        var details = "Trigger: " + trigger;
        if (started != null) {
            writeRecoveryLog([id], recoveryTitle(started) + " started for " + names([id]) + ". "
                + describeStep(started.NextStep, started.NextStepDueUtc), details);
        } else if (joined != null) {
            writeRecoveryLog([id], recoveryTitle(joined) + ": " + names([id]) + " joins the recovery already running.", details);
        } else {
            var nextChance = oldestInWindow == null ? "" : " The next one can start after " + timeOfDay(oldestInWindow.Value + recoveryWindow) + ".";
            writeRecoveryLog([id], "Auto-recovery not attempted for " + names([id]) + ": " + budget + " incident(s) in the last "
                + recoveryWindow.TotalHours.To1000N() + " hours already." + nextChance + " Restart it by hand.", details);
        }
    }

    void scheduleRecoveryStep(RecoveryIncident incident, AutoRecoveryStep step) {
        var delay = recoveryDelay;
        incident.NextStep = step;
        incident.NextStepDueUtc = DateTime.UtcNow + delay;
        _recoveryTimer?.Dispose();
        _recoveryTimer = new Timer(_ => _ = runRecoveryStepAsync(incident), null, delay, Timeout.InfiniteTimeSpan);
    }

    async Task runRecoveryStepAsync(RecoveryIncident incident) {
        AutoRecoveryStep step;
        Guid[] ids;
        lock (_recoveryLock) {
            if (_recoveryIncident != incident) return;
            _recoveryTimer?.Dispose();
            _recoveryTimer = null;
            step = incident.NextStep;
            ids = [.. incident.DatabaseIds];
        }
        try {
            await waitWhileAsync(() => IsRestarting);
            if (IsShuttingDown) {
                endRecovery(incident);
                return;
            }
            if (!Settings.AutoRecovery) {
                writeRecoveryLog(ids, recoveryTitle(incident) + " stopped: automatic recovery was switched off.", null);
                endRecovery(incident);
                return;
            }
            if (ids.All(isOpenOrRemoved)) {
                writeRecoveryLog(ids, recoveryTitle(incident) + ": " + names(ids) + " opened by other means, nothing left to recover.",
                    "Total time: " + seconds(incident.Elapsed.Elapsed));
                endRecovery(incident);
                return;
            }
            incident.StepsTaken++;
            if (step == AutoRecoveryStep.RestartProcess) {
                stopProcessForRecovery(incident, ids);
                return;
            }
            var startedUtc = DateTime.UtcNow;
            var sw = Stopwatch.StartNew();
            string? stepError = null;
            try {
                if (step == AutoRecoveryStep.RestartDatabase) await restartDatabasesForRecoveryAsync(ids);
                else await restartServerForRecoveryAsync(incident);
            } catch (Exception err) {
                stepError = err.Message;
            }
            sw.Stop();
            lock (_recoveryLock) ids = [.. incident.DatabaseIds];
            var recovered = ids.All(isOpenOrRemoved);
            AutoRecoveryStep? next = null;
            string? whyNoNext = null;
            if (!recovered) next = nextRecoveryStep(step, ids, out whyNoNext);
            if (next != null) {
                lock (_recoveryLock) {
                    if (_recoveryIncident == incident && !IsShuttingDown) scheduleRecoveryStep(incident, next.Value);
                    else next = null;
                }
            }
            var outcome = recovered ? "succeeded" : "failed";
            var sb = new StringBuilder(recoveryTitle(incident) + ", attempt " + incident.StepsTaken + ": " + stepName(step) + " " + outcome
                + " after " + seconds(sw.Elapsed) + ".");
            if (recovered) sb.Append(" Recovered in " + seconds(incident.Elapsed.Elapsed) + " in total.");
            else if (next != null) sb.Append(" " + describeStep(next.Value, incident.NextStepDueUtc));
            else sb.Append(" Giving up" + (whyNoNext == null ? "" : ": " + whyNoNext) + ". Restart it by hand.");
            writeRecoveryLog(ids, sb.ToString(), stepDetails(startedUtc, sw.Elapsed, ids, stepError));
            if (next == null) endRecovery(incident);
        } catch (Exception err) {
            writeRecoveryLog(ids, recoveryTitle(incident) + " stopped by an unexpected error: " + err.Message, err.ToString());
            endRecovery(incident);
        }
    }

    async Task restartDatabasesForRecoveryAsync(Guid[] ids) {
        foreach (var id in ids) {
            var container = containerById(id);
            if (container == null) continue;
            await waitWhileAsync(() => container.IsOpening);
            if (container.IsOpen()) continue;
            try { container.CloseIfOpen(); } catch { }
            try { openWaitingForAnotherProcessIfNeeded(container); } catch { }
        }
    }

    async Task restartServerForRecoveryAsync(RecoveryIncident incident) {
        var restarted = false;
        try {
            restarted = await softRestartAsync(recoveryTitle(incident) + ": restarting the server.");
        } catch { }
        if (!restarted) await waitWhileAsync(() => IsRestarting);
        await waitWhileAsync(() => AnyRemaingToAutoOpenIncludingFailed);
        Guid[] ids;
        lock (_recoveryLock) ids = [.. incident.DatabaseIds];
        foreach (var id in ids) {
            var container = containerById(id);
            if (container == null || container.Settings.AutoOpen || container.IsOpen() || container.IsOpening) continue;
            try {
                openWaitingForAnotherProcessIfNeeded(container);
            } catch { }
        }
        foreach (var id in ids) {
            var container = containerById(id);
            if (container != null) await waitWhileAsync(() => container.IsOpening);
        }
    }

    AutoRecoveryStep? nextRecoveryStep(AutoRecoveryStep step, Guid[] ids, out string? whyNot) {
        whyNot = null;
        if (step == AutoRecoveryStep.RestartDatabase) return AutoRecoveryStep.RestartServer;
        if (step != AutoRecoveryStep.RestartServer) return null;
        if (!Settings.AutoRecoveryRestartsProcess) {
            whyNot = "restarting the process is not switched on";
            return null;
        }
        if (_lifetime == null) {
            whyNot = "the server was started without a host that could be stopped";
            return null;
        }
        var caps = GetRestartCapabilities();
        if (caps.HostRestartsAutomatically == false) {
            whyNot = "nothing would start the process again on this host (" + caps.HostDescription + ")";
            return null;
        }
        var processRestarts = ids.Select(processRestartsInWindow).DefaultIfEmpty(0).Max();
        if (processRestarts >= recoveryIncidentsPerDay) {
            whyNot = "the process was already restarted " + processRestarts + " time(s) by auto-recovery in the last "
                + recoveryWindow.TotalHours.To1000N() + " hours";
            return null;
        }
        return AutoRecoveryStep.RestartProcess;
    }

    void stopProcessForRecovery(RecoveryIncident incident, Guid[] ids) {
        var now = DateTime.UtcNow;
        foreach (var io in distinctLogIOs(ids)) {
            try {
                using var stream = io.OpenAppend(FileKeyUtility.AutoRecoveryProcessRestartsFileKey);
                stream.WriteUTF8StringNoLengthPrefix(now.ToString("o", CultureInfo.InvariantCulture) + "\n");
            } catch { }
        }
        var caps = GetRestartCapabilities();
        writeRecoveryLog(ids, recoveryTitle(incident) + ", attempt " + incident.StepsTaken + ": " + stepName(AutoRecoveryStep.RestartProcess)
            + ". Stopping the process now, for the host to start a new one. The new process reports in this log when the database opens or fails again.",
            "Host: " + caps.HostDescription + "\nStarted: " + now.ToString("o", CultureInfo.InvariantCulture)
            + "\nTotal time before the stop: " + seconds(incident.Elapsed.Elapsed));
        endRecovery(incident);
        var lifetime = _lifetime;
        if (lifetime == null) return;
        ThreadPool.QueueUserWorkItem(_ => {
            try {
                lifetime.StopApplication();
            } catch (Exception err) {
                logRestart("Error signalling the host to stop: " + err.Message);
            }
        });
    }

    int processRestartsInWindow(Guid databaseId) {
        var container = containerById(databaseId);
        if (container == null || !tryGetLogIO(container, out var io)) return 0;
        try {
            var key = FileKeyUtility.AutoRecoveryProcessRestartsFileKey;
            var size = io.GetFileSizeOrZeroIfUnknown(key);
            if (size == 0) return 0;
            string text;
            using (var stream = io.OpenRead(key, 0)) text = stream.ReadUTF8StringNoLengthPrefix((int)stream.Length);
            var since = DateTime.UtcNow - recoveryWindow;
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Count(line => DateTime.TryParse(line, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var utc) && utc.ToUniversalTime() > since);
        } catch {
            return 0;
        }
    }

    void endRecovery(RecoveryIncident incident) {
        lock (_recoveryLock) {
            if (_recoveryIncident != incident) return;
            _recoveryIncident = null;
            _recoveryTimer?.Dispose();
            _recoveryTimer = null;
        }
    }

    void endAllRecovery() {
        RecoveryIncident? pending;
        lock (_recoveryLock) {
            pending = _recoveryIncident;
            _recoveryIncident = null;
            _recoveryTimer?.Dispose();
            _recoveryTimer = null;
        }
        if (pending != null) logRestart(recoveryTitle(pending) + " abandoned, the server is stopping.");
    }

    async Task waitWhileAsync(Func<bool> busy) {
        while (busy() && !IsShuttingDown) await Task.Delay(100);
    }

    NodeStoreContainer? containerById(Guid id) {
        lock (Containers) return Containers.TryGetValue(id, out var container) ? container : null;
    }

    bool isOpenOrRemoved(Guid id) => containerById(id) is not NodeStoreContainer container || container.IsOpen();

    bool tryGetLogIO(NodeStoreContainer container, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out IIOProvider io) {
        var s = container.Settings;
        var ioId = s.IoLog is Guid log && log != Guid.Empty ? log : s.IoDatabase is Guid database && database != Guid.Empty ? database : Guid.Empty;
        io = null;
        if (ioId == Guid.Empty) return false;
        try {
            return TryGetIO(ioId, out io);
        } catch {
            io = null;
            return false;
        }
    }

    IEnumerable<IIOProvider> distinctLogIOs(Guid[] ids) {
        var seen = new HashSet<IIOProvider>(ReferenceEqualityComparer.Instance);
        foreach (var id in ids) {
            var container = containerById(id);
            if (container != null && tryGetLogIO(container, out var io) && seen.Add(io)) yield return io;
        }
    }

    void writeRecoveryLog(Guid[] ids, string description, string? details) {
        logRestart(description);
        var now = DateTime.UtcNow;
        foreach (var io in distinctLogIOs(ids)) {
            try {
                CriticalErrorLogFile.Append(io, now, description, details);
            } catch (Exception err) {
                Log("Could not write to the critical error log: " + err.Message);
            }
        }
    }

    string stepDetails(DateTime startedUtc, TimeSpan duration, Guid[] ids, string? stepError) {
        var sb = new StringBuilder();
        sb.AppendLine("Started:  " + startedUtc.ToString("o", CultureInfo.InvariantCulture));
        sb.AppendLine("Finished: " + (startedUtc + duration).ToString("o", CultureInfo.InvariantCulture));
        sb.AppendLine("Duration: " + seconds(duration));
        if (stepError != null) sb.AppendLine("Error: " + stepError);
        foreach (var id in ids) {
            var container = containerById(id);
            if (container == null) {
                sb.AppendLine(id + ": no longer in the settings");
                continue;
            }
            sb.Append("\"" + nameOf(container) + "\": " + container.StateName);
            if (!container.IsOpen() && container.StartUpException != null) sb.Append(" - " + container.StartUpException.Message);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    string recoveryTitle(RecoveryIncident incident)
        => "Auto-recovery, incident " + incident.Number + " of " + recoveryIncidentsPerDay + " in " + recoveryWindow.TotalHours.To1000N() + " hours";

    string names(Guid[] ids) => string.Join(", ", ids.Select(id => containerById(id) is NodeStoreContainer c ? "\"" + nameOf(c) + "\"" : id.ToString()));

    static string nameOf(NodeStoreContainer container) => string.IsNullOrEmpty(container.Settings.Name) ? container.Settings.Id.ToString() : container.Settings.Name;

    static string describeStep(AutoRecoveryStep step, DateTime dueUtc) => step switch {
        AutoRecoveryStep.RestartDatabase => "Restarting the database at " + timeOfDay(dueUtc) + ".",
        AutoRecoveryStep.RestartServer => "Restarting the whole server at " + timeOfDay(dueUtc) + ".",
        _ => "Restarting the process at " + timeOfDay(dueUtc) + ".",
    };

    static string stepName(AutoRecoveryStep step) => step switch {
        AutoRecoveryStep.RestartDatabase => "restarting the database",
        AutoRecoveryStep.RestartServer => "restarting the whole server",
        _ => "restarting the process",
    };

    static string timeOfDay(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    static string seconds(TimeSpan duration) => duration.TotalSeconds.To1000N() + " s";
}
