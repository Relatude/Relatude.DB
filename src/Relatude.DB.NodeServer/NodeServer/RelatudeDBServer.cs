using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Tasks;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.NodeServer;
public partial class RelatudeDBServer {
    public RelatudeDBServer(string? urlPath) {
        if (!string.IsNullOrWhiteSpace(urlPath)) ApiUrlRoot = urlPath;
        if (ApiUrlRoot.EndsWith('/')) ApiUrlRoot = ApiUrlRoot[0..^1];
        if (!ApiUrlRoot.StartsWith('/') && ApiUrlRoot.Length > 0) ApiUrlRoot = '/' + ApiUrlRoot;
    }

    // simple startup log to help with debugging startup issues
    readonly Queue<Tuple<DateTime, string>> _serverLog = [];
    void serverLog(string msg) {
        lock (_serverLog) {
            while (_serverLog.Count >= 1000) _serverLog.Dequeue();
            _serverLog.Enqueue(new(DateTime.UtcNow, msg));
        }
    }
    Tuple<DateTime, string>[] getStartUpLog() { lock (_serverLog) { return _serverLog.ToArray(); } }
    void clearStartUpLog() { lock (_serverLog) { _serverLog.Clear(); } }

    string _settingsFile = Defaults.SettingsFileName;
    string _rootDataFolderPath = string.Empty;
    IIOProvider? _tempIO;
    ISettingsLoader? _settingsLoader;
    Dictionary<Guid, IIOProvider> _ios = [];
    Dictionary<string, IAIProvider> _ais = [];

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
    public RelatudeDBServerSettings Settings {
        get {
            return _serverSettings;
        }
    }
    Dictionary<Guid, NodeStoreContainer> _containers = [];
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
        if (!_containers.TryGetValue(storeId, out var container)) throw new Exception("Container not found.");
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
        var datastore = container.datastore;
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
    int getStartingProgressEstimate() {
        try {
            var combinedProgress = _containersToAutoOpen.Sum(c => {
                if (c.datastore == null) return 0;
                if (c.datastore.State != DataStoreState.Opening) return 100;
                var status = c.datastore.GetStatus();
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
        if (anyRemaingToAutoOpen && ctx.Request.Path == "/") {
            ctx.Response.ContentType = "text/html";
            var html = getResource("ClientStart.start.html");
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
        _tempIO = new IODisk(tempFolderPath);
        var tempFiles = _tempIO.GetFiles();
        var tempSize = tempFiles.Sum(f => f.Size);
        var tempCount = tempFiles.Length;
        if (tempCount == 0) serverLog("No temp files found to clean.");
        else serverLog($"Cleaning temp folder, found {tempCount} file(s) and {tempSize.ToByteString()}.");
        foreach (var file in tempFiles) {
            try { _tempIO.DeleteIfItExists(file.Key); } catch { }
        }
        _settingsLoader = settings == null ? new LocalSettingsLoaderFile(Path.Combine(_rootDataFolderPath, _settingsFile)) : settings;
        Stopwatch sw = Stopwatch.StartNew();
        if (tempCount == 0) serverLog("Loading settings using: " + _settingsLoader.GetType().FullName);
        _serverSettings = await _settingsLoader.ReadAsync();
        serverLog("Settings loaded in " + sw.Elapsed.TotalMilliseconds.To1000N() + " ms. Found " + (_serverSettings.ContainerSettings?.Length ?? 0) + " container(s).");
        if (_serverSettings.ContainerSettings != null) {
            foreach (var containerSettings in _serverSettings.ContainerSettings) {
                var container = new NodeStoreContainer(containerSettings, this);
                _containers.Add(containerSettings.Id, container);
                if (containerSettings.Id == _serverSettings.DefaultStoreId) _defaultContainer = container;
            }
        }
        _containersToAutoOpen = _containers.Values.Where(c => c.Settings.AutoOpen).ToArray();
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
    bool anyRemaingToAutoOpen => Interlocked.CompareExchange(ref _remaingToAutoOpenCount, 0, 0) > 0;
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

    void updateWAFServerSettingsFile() {
        _serverSettings.ContainerSettings = _containers.Values.Select(c => c.Settings).ToArray();
        _settingsLoader!.WriteAsync(_serverSettings).Wait();
        if (_containers.ContainsKey(_serverSettings.DefaultStoreId)) _defaultContainer = _containers[_serverSettings.DefaultStoreId];
    }
    void ensurePrefix(Guid storeId, ref string fileKey) {
        var filePrefix = _containers[storeId].Settings?.LocalSettings?.FilePrefix;
        if (string.IsNullOrEmpty(filePrefix)) return;
        if (!fileKey.StartsWith('.')) filePrefix += ".";
        if (fileKey.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase)) return;
        fileKey = filePrefix + fileKey;
    }
    NodeStore GetStore(Guid storeId) {
        if (!_containers.TryGetValue(storeId, out var container)) throw new Exception("Container not found.");
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
}
