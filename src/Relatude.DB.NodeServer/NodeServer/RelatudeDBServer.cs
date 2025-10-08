using Microsoft.AspNetCore.Hosting.Server;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer.EventHub;
using Relatude.DB.NodeServer.EventTriggers;
using Relatude.DB.Tasks;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.NodeServer;
/// <summary>
/// Represents the main server for managing and interacting with Relatude database containers and services.
/// </summary>
/// <remarks>The <see cref="RelatudeDBServer"/> class provides functionality to initialize, configure, and manage
/// database containers, authentication, and other server-related operations. It supports automatic opening of database
/// containers, event handling for store lifecycle events, and integration with I/O and AI providers.  This class is
/// designed to be the central entry point for interacting with the Relatude database system. It includes methods for
/// starting the server, managing containers, and retrieving resources such as I/O and AI providers.  <para> To use this
/// class, ensure that the server is properly initialized by calling <see cref="StartAsync"/>. Attempting to access
/// certain properties or methods before initialization may result in exceptions. </para></remarks>
public partial class RelatudeDBServer {
    public RelatudeDBServer(string? urlPath) {
        if (!string.IsNullOrWhiteSpace(urlPath)) ApiUrlRoot = urlPath;
        if (ApiUrlRoot.EndsWith('/')) ApiUrlRoot = ApiUrlRoot[0..^1];
        if (!ApiUrlRoot.StartsWith('/') && ApiUrlRoot.Length > 0) ApiUrlRoot = '/' + ApiUrlRoot;
        EventHub = new ServerEventHub(this);
        EventHub.RegisterPoller(new ServerStatusEventPoller());
        EventHub.RegisterPoller(new DataStoreStatusEventPoller());
    }

    // simple startup log to help with debugging startup issues
    readonly Queue<Tuple<DateTime, string>> _serverLog = [];
    void serverLog(string msg) {
        lock (_serverLog) {
            while (_serverLog.Count >= 1000) _serverLog.Dequeue();
            _serverLog.Enqueue(new(DateTime.UtcNow, msg));
        }
    }
    public Tuple<DateTime, string>[] GetStartUpLog() { lock (_serverLog) { return _serverLog.ToArray(); } }
    public void ClearStartUpLog() { lock (_serverLog) { _serverLog.Clear(); } }

    static object _traceLock = new ();
    public static void Trace(string msg) {
        lock(_traceLock){
            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.Write("relatude.server: ");
            Console.ResetColor();
            Console.WriteLine(msg);
        }
    }

    ServerAPI? _api;
    string _settingsFile = Defaults.SettingsFileName;
    string _rootDataFolderPath = string.Empty;
    public IIOProvider? TempIO;
    ISettingsLoader? _settingsLoader;
    Dictionary<Guid, IIOProvider> _ios = [];
    Dictionary<string, IAIProvider> _ais = [];
    public void ResetIOAndAIProviders() {
        lock (_ios) _ios.Clear();
        lock (_ais) {
            foreach (var ai in _ais.Values) ai.Dispose();
            _ais.Clear();
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public static event EventHandler<NodeStore>? OnStoreInit;
    public static event EventHandler<NodeStore>? OnStoreOpen;
    public static event EventHandler<NodeStore>? OnStoreDispose;
    SimpleAuthentication? _autentication;
    public SimpleAuthentication Authentication {
        get {
            if (_autentication == null) throw new Exception("Authentication not initialized. Make sure to call RelatudeDBServer.StartAsync() before using the server.");
            return _autentication;
        }
    }
    internal string RootDataFolderPath => _rootDataFolderPath;
    internal string ApiUrlRoot { get; private set; } = string.Empty;
    internal string ApiUrlPublic => ApiUrlRoot + "/auth/";
    RelatudeDBServerSettings _serverSettings = new() { Id = Guid.NewGuid(), Name = "Relatude.DB Server" };
    public RelatudeDBServerSettings Settings => _serverSettings;
    internal ServerEventHub EventHub { get; }
    public Dictionary<Guid, NodeStoreContainer> Containers = [];
    NodeStoreContainer[] _containersToAutoOpen = [];
    NodeStoreContainer? _defaultContainer = null;
    public bool DefaultStoreIsOpenOrOpening() => _defaultContainer != null && _defaultContainer.IsOpenOrOpening();
    public NodeStoreContainer? DefaultContainer => _defaultContainer;
    public NodeStore Default => GetDefaultStoreAndWaitIfOpening();
    public NodeStore GetDefaultStoreAndWaitIfOpening(int timeoutSec = 60 * 15) {
        if (_defaultContainer == null) throw new Exception("No default store container found or initialized. ");
        return GetStoreAndWaitIfOpening(_defaultContainer.Settings.Id, timeoutSec);
    }
    public NodeStore GetStoreAndWaitIfOpening(Guid storeId, int timeoutSec = 60 * 15) {
        if (!Containers.TryGetValue(storeId, out var container)) throw new Exception("Container not found.");
        var sw = Stopwatch.StartNew();
        if (container.Settings.AutoOpen) { // if auto open is enabled, we have to wait for first initialization, otherwise .datastore will be null
            while (true) {
                if (container.StartUpException != null)
                    throw new Exception("Unable to open database.", container.StartUpException);
                if (container.HasInitialized) break;
                if (sw.Elapsed.TotalSeconds > timeoutSec) throw new Exception("Timeout waiting for store to start initializing.");
                Thread.Sleep(100);
            }
        }
        var datastore = container.Datastore;
        if (datastore == null) throw new Exception("Store not initialized. ");
        if (datastore.State != DataStoreState.Opening && container.Store != null) return container.Store;
        //sw.Restart();
        while (true) {
            if (container.StartUpException != null) throw new Exception("Unable to autostart database.", container.StartUpException);
            if (datastore.State != DataStoreState.Opening && container.Store != null) return container.Store;
            if (sw.Elapsed.TotalSeconds > timeoutSec) throw new Exception("Timeout waiting for store to open.");
            Thread.Sleep(100);
        }
    }
    public int GetStartingProgressEstimate() {
        try {
            var combinedProgress = _containersToAutoOpen.Sum(c => {
                if (c.Datastore == null) return 0;
                if (c.Datastore.State != DataStoreState.Opening) return 100;
                var status = c.Datastore.GetStatus();
                var activity = status.ActivityTree.FirstOrDefault(a => a.Activity.Category == DataStoreActivityCategory.Opening)?.Activity;
                if (activity == null) return 100;
                var progress = activity.PercentageProgress;
                if (!progress.HasValue) return 0;
                return progress.Value;
            });
            return (int)Math.Ceiling((double)combinedProgress / _containersToAutoOpen.Length);
        } catch (Exception err) {
            serverLog("Error occurred during progress estimate: " + err.Message);
            return 0;
        }
    }
    public async Task StartupProgressBarMiddleware(HttpContext ctx, Func<Task> next) {
        if (AnyRemaingToAutoOpen && ctx.Request.Path == "/") {
            ctx.Response.ContentType = "text/html";
            var html = ServerAPI.GetResource("ClientStart.start.html");
            await ctx.Response.WriteAsync(html);
        } else {
            await next();
        }
    }
    public async Task StartAsync(WebApplication app, string? dataFolderPath, string? tempFolderPath = null, ISettingsLoader? settings = null) {
        _serverLog.Clear();
        serverLog("Server starting up.");
        var environmentRoot = app.Environment.ContentRootPath;
        if (string.IsNullOrEmpty(dataFolderPath)) dataFolderPath = string.Empty;
        dataFolderPath = dataFolderPath.EnsureDirectorySeparatorChar();
        if (!System.IO.Path.IsPathRooted(dataFolderPath)) dataFolderPath = environmentRoot.SuperPathCombine(dataFolderPath);
        _rootDataFolderPath = dataFolderPath;

        if (tempFolderPath == null) tempFolderPath = Defaults.TempFolderPath;
        if (!Path.IsPathRooted(tempFolderPath)) tempFolderPath = environmentRoot.SuperPathCombine(tempFolderPath);
        TempIO = new IODisk(tempFolderPath);
        var tempFiles = TempIO.GetFiles();
        var tempSize = tempFiles.Sum(f => f.Size);
        var tempCount = tempFiles.Length;
        if (tempCount == 0) serverLog("No temp files found to clean.");
        else serverLog($"Cleaning temp folder, found {tempCount} file(s) and {tempSize.ToByteString()}.");
        foreach (var file in tempFiles) {
            try { TempIO.DeleteIfItExists(file.Key); } catch { }
        }
        _settingsLoader = settings == null ? new LocalSettingsLoaderFile(Path.Combine(_rootDataFolderPath, _settingsFile)) : settings;
        Stopwatch sw = Stopwatch.StartNew();
        if (tempCount == 0) serverLog("Loading settings using: " + _settingsLoader.GetType().FullName);
        _serverSettings = await _settingsLoader.ReadAsync();
        serverLog("Settings loaded in " + sw.Elapsed.TotalMilliseconds.To1000N() + " ms. Found " + (_serverSettings.ContainerSettings?.Length ?? 0) + " container(s).");
        if (_serverSettings.ContainerSettings != null) {
            foreach (var containerSettings in _serverSettings.ContainerSettings) {
                var container = new NodeStoreContainer(containerSettings, this);
                Containers.Add(containerSettings.Id, container);
                if (containerSettings.Id == _serverSettings.DefaultStoreId) _defaultContainer = container;
            }
        }
        _containersToAutoOpen = Containers.Values.Where(c => c.Settings.AutoOpen).ToArray();
        serverLog("AutoOpen is enabled for " + _containersToAutoOpen.Length + " database(s).");
        _remaingToAutoOpenCount = _containersToAutoOpen.Length;
        foreach (var container in _containersToAutoOpen) {
            if (container.Settings.WaitUntilOpen) {
                serverLog("Opening \"" + container.Settings.Name + "\".");
                autoOpenContainer(container, true);
            } else {
                serverLog("Initiating asynchronous opening of \"" + container.Settings.Name + "\".");
                ThreadPool.QueueUserWorkItem((NodeStoreContainer container) => autoOpenContainer(container, false), container, true);
            }
        }
        _autentication = new(this);
    }
    int _remaingToAutoOpenCount = 0;
    public bool AnyRemaingToAutoOpen => Interlocked.CompareExchange(ref _remaingToAutoOpenCount, 0, 0) > 0;
    void autoOpenContainer(NodeStoreContainer container, bool throwException) {
        try {
            var sw = Stopwatch.StartNew();
            container.StartUpException = null;
            container.StartUpExceptionDateTimeUTC = null;
            container.Open();
            serverLog("Database \"" + container.Settings.Name + "\" opened in " + sw.Elapsed.TotalMilliseconds.To1000N() + " ms.");
        } catch (Exception err) {
            container.StartUpException = err;
            container.StartUpExceptionDateTimeUTC = DateTime.UtcNow;
            serverLog("An error occurred while opening \"" + container.Settings.Name + "\". " + err.Message);
            Console.WriteLine(err.Message); //
            if (throwException) throw;
        } finally {
            Interlocked.Decrement(ref _remaingToAutoOpenCount);
        }
    }

    Dictionary<Guid, List<ITaskRunner>> _runnersByContainer = [];
    public void RegisterTaskRunner(ITaskRunner runner) {
        RegisterTaskRunner(Guid.Empty, runner); // meaning for all containers
    }
    public void RegisterTaskRunner(Guid containerId, ITaskRunner runner) {
        lock (_runnersByContainer) {
            if (!_runnersByContainer.TryGetValue(containerId, out var runners)) {
                runners = [];
                _runnersByContainer[containerId] = runners;
            }
            runners.Add(runner);
        }
    }
    internal IEnumerable<ITaskRunner> GetRegisteredTaskRunners(NodeStoreContainer container) {
        lock (_runnersByContainer) {
            List<ITaskRunner> values = [];
            if (_runnersByContainer.TryGetValue(container.Settings.Id, out var runners)) values.AddRange(runners);
            if (_runnersByContainer.TryGetValue(Guid.Empty, out var allRunners)) values.AddRange(allRunners);
            return values;
        }
    }

    public void UpdateWAFServerSettingsFile() {
        _serverSettings.ContainerSettings = Containers.Values.Select(c => c.Settings).ToArray();
        _settingsLoader!.WriteAsync(_serverSettings).Wait();
        if (Containers.ContainsKey(_serverSettings.DefaultStoreId)) _defaultContainer = Containers[_serverSettings.DefaultStoreId];
    }
    public NodeStore GetStore(Guid storeId) {
        if (!Containers.TryGetValue(storeId, out var container)) throw new Exception("Container not found.");
        if (container.Store == null) throw new Exception("Store not initialized. ");
        return container.Store;
    }
    internal void RaiseEventStoreOpen(NodeStoreContainer nodeStoreContainer, NodeStore store) {
        if (nodeStoreContainer == null) return;
        try {
            OnStoreOpen?.Invoke(nodeStoreContainer, store);
        } catch (Exception err) {
            serverLog("Error occurred during OnStoreOpen event: " + err.Message);
        }
    }
    internal void RaiseEventStoreInit(NodeStoreContainer nodeStoreContainer, NodeStore store) {
        if (nodeStoreContainer == null) return;
        try {
            OnStoreInit?.Invoke(nodeStoreContainer, store);
        } catch (Exception err) {
            serverLog("Error occurred during OnStoreInit event: " + err.Message);
        }
    }
    internal void RaiseEventStoreDispose(NodeStoreContainer nodeStoreContainer, NodeStore store) {
        if (nodeStoreContainer == null) return;
        try {
            OnStoreDispose?.Invoke(nodeStoreContainer, store);
        } catch (Exception err) {
            serverLog("Error occurred during OnStoreDispose event: " + err.Message);
        }
    }
    public bool TryGetIO(Guid ioId, [MaybeNullWhen(false)] out IIOProvider io) {
        lock (_ios) {
            if (_ios.TryGetValue(ioId, out io)) return true;
            var settings = _serverSettings.ContainerSettings?.SelectMany(c => c.IOSettings!)?.FirstOrDefault(s => s.Id == ioId);
            if (settings == null) return false;
            io = IOSettings.Create(settings, _rootDataFolderPath);
            try {
                _ios.Add(ioId, io);
            } catch (Exception ex) {
                var msg = $"Failed to create IOProvider {settings.Name} [{ioId}]: {ex.Message}";
                throw new Exception(msg, ex);
            }
            return _ios.TryGetValue(ioId, out io);
        }
    }
    public bool TryGetAI(Guid id, string? filePrefix, [MaybeNullWhen(false)] out IAIProvider ai, string? fallBackAiPath) {
        lock (_ais) {
            if (_ais.TryGetValue(id + filePrefix, out ai)) return true;
            var settings = _serverSettings.AISettings?.FirstOrDefault(s => s.Id == id);
            if (settings == null) return false;
            string? folderPath = settings.FilePath;
            if (string.IsNullOrEmpty(folderPath)) folderPath = fallBackAiPath;
            if (!string.IsNullOrEmpty(folderPath)) {
                if (!Path.IsPathRooted(folderPath)) {
                    folderPath = _rootDataFolderPath.SuperPathCombine(folderPath);
                }
                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
            } else {
                throw new Exception($"AIProvider {settings.Name} [{id}] does not have a valid file path set.");
            }
            ai = AISettings.Create(settings, folderPath, filePrefix);
            try {
                _ais.Add(id + filePrefix, ai);
            } catch (Exception ex) {
                var msg = $"Failed to create AIProvider {settings.Name} [{id}]: {ex.Message}";
                throw new Exception(msg, ex);
            }
            return _ais.TryGetValue(id + filePrefix, out ai);
        }
    }
    public IIOProvider GetIO(Guid id) {
        if (!TryGetIO(id, out var io)) throw new Exception("IOProvider not found");
        return io;
    }
    public IAIProvider GetAI(Guid id, string? filePrefix, string? fallBackAiPath) {
        if (!TryGetAI(id, filePrefix, out var ai, fallBackAiPath)) throw new Exception("AIProvider not found");
        return ai;
    }
    internal void MapSimpleAPI(WebApplication app) {
        if (_api != null) throw new Exception("API already mapped.");
        _api = new ServerAPI(this);
        _api.MapSimpleAPI(app);
    }
}
