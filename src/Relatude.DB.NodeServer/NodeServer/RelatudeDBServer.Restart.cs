using Relatude.DB.Common;
using System.Diagnostics;
namespace Relatude.DB.NodeServer;

/// <summary>
/// Which kinds of restart the admin UI is allowed to trigger. Both sit behind the admin
/// authentication, so both are allowed by default.
/// </summary>
[Flags]
public enum RestartOptions {
    None = 0,
    /// <summary>Reload the settings and rebuild the databases without touching the process.</summary>
    Soft = 1,
    /// <summary>Stop the host gracefully and leave it to the platform to start a new process.</summary>
    StopHost = 2,
    All = Soft | StopHost,
}

/// <summary>
/// What the running host can do about a restart, and what it will look like from the outside. Handed
/// to the admin UI so that the confirmation can say what is actually going to happen, and so that it
/// can tell afterwards whether the restart landed.
/// </summary>
public class RestartCapabilities {
    public bool CanSoftRestart { get; set; }
    public bool CanStopHost { get; set; }
    /// <summary>Best guess at what is hosting the process, for display only.</summary>
    public string HostDescription { get; set; } = "Unknown";
    /// <summary>
    /// Whether stopping the host is expected to bring a new process back. Null when the host could not
    /// be identified, which is the case worth warning about: nothing may be watching.
    /// </summary>
    public bool? HostRestartsAutomatically { get; set; }
    /// <summary>What to warn about before stopping the host, or null when there is nothing to say.</summary>
    public string? StopHostWarning { get; set; }
    /// <summary>New for every process, so a change proves a new process really did start.</summary>
    public Guid InstanceId { get; set; }
    /// <summary>Soft restarts this process has completed, so a change proves a reload landed.</summary>
    public int RestartCount { get; set; }
    public bool IsRestarting { get; set; }
    public bool IsShuttingDown { get; set; }
    public double UpTimeInMs { get; set; }
}

public partial class RelatudeDBServer {

    /// <summary>
    /// Identifies this process. Regenerated on every start, which is what lets the admin UI tell a
    /// process that restarted from one that only reloaded.
    /// </summary>
    public Guid InstanceId { get; } = Guid.NewGuid();

    int _restartCount = 0;
    /// <summary>Soft restarts completed since the process started.</summary>
    public int RestartCount => Volatile.Read(ref _restartCount);

    DateTime? _lastRestartUtc;
    public DateTime? LastRestartUtc => _lastRestartUtc;

    // 0 = normal, 1 = a soft restart is tearing down and rebuilding
    int _restartPhase = 0;
    /// <summary>
    /// True while a soft restart is between closing the old databases and opening the new ones. The
    /// middleware holds application requests off for exactly this window.
    /// </summary>
    public bool IsRestarting => Volatile.Read(ref _restartPhase) > 0;

    int _requestsInFlight = 0;
    /// <summary>
    /// Application requests currently inside the pipeline. Requests to the admin API are deliberately
    /// not counted: the restart is triggered and watched from there, and the event stream the admin UI
    /// holds open never completes, so counting those would mean the drain could never reach zero.
    /// </summary>
    public int RequestsInFlight => Volatile.Read(ref _requestsInFlight);
    internal void EnterRequest() => Interlocked.Increment(ref _requestsInFlight);
    internal void ExitRequest() => Interlocked.Decrement(ref _requestsInFlight);

    /// <summary>
    /// Rebuilds the server in place: the settings file and the configuration overlay are read again,
    /// every database is closed and rebuilt from the new settings, and the ones marked AutoOpen are
    /// reopened. The process is never touched, which makes this the only restart that behaves the same
    /// on every host, a plain <c>dotnet run</c> included.
    /// <para>What it cannot pick up, because those are fixed when the process starts: <see cref="ServerOptions"/>,
    /// the admin UI URL path (the routes are already mapped), DI singletons, environment variables and
    /// any new build of the application. Those need <see cref="StopHost"/> and a supervisor.</para>
    /// <para>Returns false when a restart is already running or the host is stopping. Runs long enough
    /// that callers on a request thread should start it on a background thread and watch
    /// <see cref="RestartCount"/> instead of waiting for it.</para>
    /// </summary>
    public async Task<bool> SoftRestartAsync() {
        if (!AllowedRestarts.HasFlag(RestartOptions.Soft)) throw new InvalidOperationException("Soft restart is disabled by ServerOptions.AllowedRestarts. ");
        if (IsShuttingDown) return false;
        if (Interlocked.CompareExchange(ref _restartPhase, 1, 0) != 0) return false; // one at a time
        var sw = Stopwatch.StartNew();
        var timeout = Options?.ShutdownTimeout ?? ServerOptions.DefaultShutdownTimeout;
        try {
            logRestart("Soft restart requested. Holding application requests.");
            drainRequests(timeout / 4);
            // a database still replaying its log cannot be flushed, and disposing it pulls the WAL and
            // the indexes out from under the opening thread - the same reason the shutdown waits
            waitForAutoOpenToComplete(timeout);
            var stillClosing = closeAllContainers(timeout - sw.Elapsed);
            if (stillClosing > 0) {
                logRestart("Timed out waiting for " + stillClosing + " database(s) to close. They are left to the log"
                    + " replay when they are opened again.");
            }
            lock (Containers) Containers.Clear();
            _defaultContainer = null;
            _containersToAutoOpen = [];
            ResetIOProviders();
            await loadSettingsAndCreateContainersAsync(firstStart: false);
            Interlocked.Increment(ref _restartCount);
            _lastRestartUtc = DateTime.UtcNow;
            prepareAutoOpen(); // raises the opening count, so requests meet the progress page and not an empty server
        } catch (Exception err) {
            logRestart("Soft restart failed: " + err.Message);
            Volatile.Write(ref _restartPhase, 0);
            throw;
        }
        // the gate has to come down before the databases open, because opening is what the requests
        // held behind it are waiting for
        Volatile.Write(ref _restartPhase, 0);
        runAutoOpen();
        logRestart("Soft restart completed in " + sw.Elapsed.TotalSeconds.To1000N() + " s.");
        return true;
    }

    /// <summary>
    /// Stops the host gracefully: the web server drains its requests, the lifetime callbacks close the
    /// databases and the process exits.
    /// <para>Nothing here starts it again. Whether the application comes back is entirely up to whatever
    /// supervises the process - App Service and the container platforms do, a plain <c>dotnet run</c>
    /// does not. Ask <see cref="GetRestartCapabilities"/> before offering this.</para>
    /// <para>Returns immediately and signals the stop from a background thread a moment later, so that
    /// the request that asked for it can still send its response.</para>
    /// </summary>
    public bool StopHost() {
        if (!AllowedRestarts.HasFlag(RestartOptions.StopHost)) throw new InvalidOperationException("Stopping the host is disabled by ServerOptions.AllowedRestarts. ");
        if (_lifetime == null) {
            logRestart("Cannot stop the host: the server was started without a WebApplication, so there is no host to signal.");
            return false;
        }
        if (IsShuttingDown) return true; // already on its way out
        var caps = GetRestartCapabilities();
        logRestart("Host stop requested from the admin UI. Host looks like: " + caps.HostDescription + ". "
            + caps.HostRestartsAutomatically switch {
                true => "A new process is expected to start automatically.",
                false => "Nothing was detected that would start it again.",
                _ => "Whether anything will start it again could not be determined.",
            });
        var lifetime = _lifetime;
        ThreadPool.QueueUserWorkItem(_ => {
            Thread.Sleep(500); // let the response reach the admin UI before the pipeline starts tearing down
            try {
                lifetime.StopApplication();
            } catch (Exception err) {
                logRestart("Error signalling the host to stop: " + err.Message);
            }
        });
        return true;
    }

    RestartOptions AllowedRestarts => Options?.AllowedRestarts ?? RestartOptions.All;

    public RestartCapabilities GetRestartCapabilities() {
        var caps = new RestartCapabilities {
            CanSoftRestart = AllowedRestarts.HasFlag(RestartOptions.Soft),
            CanStopHost = AllowedRestarts.HasFlag(RestartOptions.StopHost) && _lifetime != null,
            InstanceId = InstanceId,
            RestartCount = RestartCount,
            IsRestarting = IsRestarting,
            IsShuttingDown = IsShuttingDown,
            UpTimeInMs = UpTime.TotalMilliseconds,
        };
        describeHost(caps);
        return caps;
    }

    /// <summary>
    /// Guesses what is hosting the process from the environment variables the platforms inject, so that
    /// the admin UI can warn before a stop that nothing would recover from. Only ever used for the
    /// warning text: a wrong guess costs a misleading message, not a broken restart.
    /// </summary>
    static void describeHost(RestartCapabilities caps) {
        var containerApp = Environment.GetEnvironmentVariable("CONTAINER_APP_NAME");
        var appService = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
        var kubernetes = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") != null;
        var inContainer = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
        var behindAncm = Environment.GetEnvironmentVariable("ASPNETCORE_ANCM_HTTPS_PORT") != null
            || Environment.GetEnvironmentVariable("ASPNETCORE_IIS_HTTPAUTH") != null
            || Environment.GetEnvironmentVariable("ASPNETCORE_TOKEN") != null;

        if (!string.IsNullOrEmpty(containerApp)) {
            caps.HostDescription = "Azure Container Apps (" + containerApp + ")";
            caps.HostRestartsAutomatically = true;
            caps.StopHostWarning = "Only this replica stops. The revision keeps running on the others.";
        } else if (!string.IsNullOrEmpty(appService)) {
            caps.HostDescription = "Azure App Service (" + appService + ")";
            caps.HostRestartsAutomatically = true;
            caps.StopHostWarning = "Only this instance stops, so on a scaled out plan the others keep running the old process. "
                + "With \"Always On\" off the site stays down until the next request arrives. "
                + "Use the Azure portal, or the management API, to restart every instance at once.";
        } else if (kubernetes) {
            caps.HostDescription = "Kubernetes";
            caps.HostRestartsAutomatically = true;
            caps.StopHostWarning = "The pod comes back only if its restart policy allows it, and only this replica stops.";
        } else if (inContainer) {
            caps.HostDescription = "Container";
            caps.HostRestartsAutomatically = null;
            caps.StopHostWarning = "The container stops. It comes back only if it was started with a restart policy.";
        } else if (behindAncm) {
            caps.HostDescription = "IIS / ASP.NET Core Module";
            caps.HostRestartsAutomatically = true;
            caps.StopHostWarning = "IIS starts a new worker process on the next request.";
        } else {
            caps.HostDescription = "Console or unknown host";
            caps.HostRestartsAutomatically = false;
            caps.StopHostWarning = "Nothing was detected that would start the application again. The process will exit and stay "
                + "down until someone starts it by hand. A soft restart is almost certainly what you want here.";
        }
    }

    /// <summary>
    /// Waits for the application requests that were already running when the gate went up, so that the
    /// databases are not disposed underneath them. Best effort: a request that outlives the timeout gets
    /// an exception from the store it was using, which is what a host shutdown would have given it too.
    /// </summary>
    void drainRequests(TimeSpan timeout) {
        if (RequestsInFlight == 0) return;
        var sw = Stopwatch.StartNew();
        while (RequestsInFlight > 0 && sw.Elapsed < timeout) Thread.Sleep(10);
        if (RequestsInFlight > 0) {
            logRestart("Timed out after " + sw.Elapsed.TotalSeconds.To1000N() + " s waiting for " + RequestsInFlight
                + " request(s) to finish. Restarting anyway.");
        } else {
            logRestart("Application requests drained in " + sw.Elapsed.TotalMilliseconds.To1000N() + " ms.");
        }
    }

    // a restart is worth seeing in the console as well as in the admin UI, and the second half of a
    // host stop happens after the admin UI has lost its connection
    void logRestart(string msg) {
        Log(msg);
        Trace(msg);
    }
}
