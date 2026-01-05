using Microsoft.AspNetCore.Hosting.Server;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer.EventHub;
using Relatude.DB.NodeServer.EventTriggers;
using Relatude.DB.NodeServer.Settings;
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
    DateTime _initialized = DateTime.UtcNow;
    public RelatudeDBServer(string? urlPath) {
        if (!string.IsNullOrWhiteSpace(urlPath)) ApiUrlRoot = urlPath;
        if (ApiUrlRoot.EndsWith('/')) ApiUrlRoot = ApiUrlRoot[0..^1];
        if (!ApiUrlRoot.StartsWith('/') && ApiUrlRoot.Length > 0) ApiUrlRoot = '/' + ApiUrlRoot;
        EventHub = new ServerEventHub(this);
        EventHub.RegisterPoller(new DataStoreStatesEventPoller());
        EventHub.RegisterPoller(new DataStoreStatusEventPoller());
        EventHub.RegisterPoller(new DataStoreInfoEventPoller());
        EventHub.RegisterPoller(new DataStoreTraceEventPoller());
    }
    public TimeSpan UpTime => DateTime.UtcNow - _initialized;
    // simple startup log to help with debugging startup issues
    readonly Queue<Tuple<DateTime, string>> _serverLog = [];
    public void Log(string msg) {
        lock (_serverLog) {
            while (_serverLog.Count >= 1000) _serverLog.Dequeue();
            _serverLog.Enqueue(new(DateTime.UtcNow, msg));
        }
    }
    public Tuple<DateTime, string>[] GetStartUpLog() { lock (_serverLog) { return _serverLog.ToArray(); } }
    public void ClearStartUpLog() { lock (_serverLog) { _serverLog.Clear(); } }

    static object _traceLock = new();
    public static void Trace(string msg) {
        lock (_traceLock) {
            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.Write("relatude.server: ");
            Console.ResetColor();
            Console.WriteLine(msg);
        }
    }

    ServerAPIMapper? _api;
    string _settingsFile = Defaults.SettingsFileName;
    string _rootDataFolderPath = string.Empty;
    IIOProvider? _tempIO;
    public IIOProvider TempIO => Validator.ThrowIfNull(_tempIO);
    ISettingsLoader? _settingsLoader;
    Dictionary<Guid, IIOProvider> _ios = [];
    Dictionary<string, AIEngine> _ais = [];
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
    SimpleAuthentication? _authentication;
    public SimpleAuthentication Authentication {
        get {
            if (_authentication == null) throw new Exception("Authentication not initialized. Make sure to call RelatudeDBServer.StartAsync() before using the server.");
            return _authentication;
        }
    }
    internal string RootDataFolderPath => _rootDataFolderPath;
    internal string DefaultSubDataFolderPath => Path.Combine(_rootDataFolderPath, Defaults.DataFolderPath);
    internal string ApiUrlRoot { get; private set; } = string.Empty;
    internal string ApiUrlPublic => ApiUrlRoot + "/auth/";
    RelatudeDBServerSettings _serverSettings = new() { Id = Guid.NewGuid(), Name = "Relatude.DB Server" };
    public RelatudeDBServerSettings Settings => _serverSettings;
    internal ServerEventHub EventHub { get; }
    public Dictionary<Guid, NodeStoreContainer> Containers = [];
    NodeStoreContainer[] _containersToAutoOpen = [];
    NodeStoreContainer? _defaultContainer = null;
    public bool DefaultStoreIsOpenOrOpening() => _defaultContainer != null && _defaultContainer.IsOpenOrOpening();
    public bool DefaultStoreIsOpen() => _defaultContainer != null && _defaultContainer.IsOpen();
    public NodeStoreContainer? DefaultContainer => _defaultContainer;
    public DataStoreOpeningStatus GetOpeningStatus() {
        try {
            if (_defaultContainer?.Store == null) return new DataStoreOpeningStatus(0, 0);
            if (_defaultContainer.Store.Datastore.State != DataStoreState.Opening) return new DataStoreOpeningStatus(100, 0);
            return _defaultContainer.Store.Datastore.GetOpeningStatus();
        } catch (Exception err) {
            Log("Error occurred during progress estimate: " + err.Message);
            return new DataStoreOpeningStatus(0, 0);
        }
    }
    public async Task StartAsync(WebApplication app, string? dataFolderPath, string? tempFolderPath = null, ISettingsLoader? settings = null) {
        _serverLog.Clear();
        Log("Server starting up.");
        var environmentRoot = app.Environment.ContentRootPath;
        if (string.IsNullOrEmpty(dataFolderPath)) dataFolderPath = string.Empty;
        dataFolderPath = dataFolderPath.EnsureDirectorySeparatorChar();
        if (!System.IO.Path.IsPathRooted(dataFolderPath)) dataFolderPath = environmentRoot.SuperPathCombine(dataFolderPath);
        _rootDataFolderPath = dataFolderPath;

        if (tempFolderPath == null) tempFolderPath = Defaults.TempFolderPath;
        if (!Path.IsPathRooted(tempFolderPath)) tempFolderPath = environmentRoot.SuperPathCombine(tempFolderPath);
        _tempIO = new IOProviderDisk(tempFolderPath);
        var tempFiles = _tempIO.GetFiles();
        var tempSize = tempFiles.Sum(f => f.Size);
        var tempCount = tempFiles.Length;
        if (tempCount == 0) Log("No temp files found to clean.");
        else Log($"Cleaning temp folder, found {tempCount} file(s) and {tempSize.ToByteString()}.");
        foreach (var file in tempFiles) {
            try { TempIO.DeleteIfItExists(file.Key); } catch { }
        }
        _settingsLoader = settings == null ? new LocalSettingsLoaderFile(Path.Combine(_rootDataFolderPath, _settingsFile)) : settings;
        Stopwatch sw = Stopwatch.StartNew();
        if (tempCount == 0) Log("Loading settings using: " + _settingsLoader.GetType().FullName);
        _serverSettings = await _settingsLoader.ReadAsync();
        Log("Settings loaded in " + sw.Elapsed.TotalMilliseconds.To1000N() + " ms. Found " + (_serverSettings.ContainerSettings?.Length ?? 0) + " container(s).");
        if (_serverSettings.ContainerSettings != null) {
            foreach (var containerSettings in _serverSettings.ContainerSettings) {
                var container = new NodeStoreContainer(containerSettings, this);
                Containers.Add(containerSettings.Id, container);
                if (containerSettings.Id == _serverSettings.DefaultStoreId) _defaultContainer = container;
            }
        }
        _containersToAutoOpen = Containers.Values.Where(c => c.Settings.AutoOpen).ToArray();
        Log("AutoOpen is enabled for " + _containersToAutoOpen.Length + " database(s).");
        _remaingToAutoOpenCount = _containersToAutoOpen.Length;
        foreach (var container in _containersToAutoOpen) {
            if (container.Settings.WaitUntilOpen) {
                Log("Opening \"" + container.Settings.Name + "\".");
                autoOpenContainer(container, true);
            } else {
                Log("Initiating asynchronous opening of \"" + container.Settings.Name + "\".");
                ThreadPool.QueueUserWorkItem((NodeStoreContainer container) => autoOpenContainer(container, false), container, true);
            }
        }
        _authentication = new(this);
    }
    int _remaingToAutoOpenCount = 0;
    public bool AnyRemaingToAutoOpenIncludingFailed => Interlocked.CompareExchange(ref _remaingToAutoOpenCount, 0, 0) > 0;
    void autoOpenContainer(NodeStoreContainer container, bool throwException) {
        try {
            var sw = Stopwatch.StartNew();
            container.StartUpException = null;
            container.StartUpExceptionDateTimeUTC = null;
            container.Open();
            Log("Database \"" + container.Settings.Name + "\" opened in " + sw.Elapsed.TotalMilliseconds.To1000N() + " ms.");
        } catch (Exception err) {
            container.StartUpException = err;
            container.StartUpExceptionDateTimeUTC = DateTime.UtcNow;
            Log("An error occurred while opening \"" + container.Settings.Name + "\". " + err.Message);
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
        if (OnStoreOpen == null) return;
        ThreadPool.QueueUserWorkItem((_) => {
            try {
                if (nodeStoreContainer == null) return;
                OnStoreOpen?.Invoke(nodeStoreContainer, store);
            } catch (Exception err) {
                Log("Error occurred during OnStoreOpen event: " + err.Message);
            }
        });
    }
    internal void RaiseEventStoreInit(NodeStoreContainer nodeStoreContainer, NodeStore store) {
        if (nodeStoreContainer == null) return;
        if (OnStoreOpen == null) return;
        ThreadPool.QueueUserWorkItem((_) => {
            try {
                if (nodeStoreContainer == null) return;
                OnStoreInit?.Invoke(nodeStoreContainer, store);
            } catch (Exception err) {
                Log("Error occurred during OnStoreInit event: " + err.Message);
            }
        });
    }
    internal void RaiseEventStoreClose(NodeStoreContainer nodeStoreContainer, NodeStore store) {
        if (OnStoreOpen == null) return;
        ThreadPool.QueueUserWorkItem((_) => {
            try {
                if (nodeStoreContainer == null) return;
                OnStoreDispose?.Invoke(nodeStoreContainer, store);
            } catch (Exception err) {
                Log("Error occurred during OnStoreDispose event: " + err.Message);
            }
        });
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
    public bool TryGetAI(Guid id, string? filePrefix, [MaybeNullWhen(false)] out AIEngine ai, string? fallBackAiPath) {
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
            ai = AIProviderFactory.Create(settings, folderPath, filePrefix);
            try {
                _ais.Add(id + filePrefix, ai);
            } catch (Exception ex) {
                var msg = $"Failed to create AIProvider {settings.Name} [{id}]: {ex.Message}";
                throw new Exception(msg, ex);
            }
            return _ais.TryGetValue(id + filePrefix, out ai);
        }
    }
    public IIOProvider? GetOrNullIO(Guid? id) {
        if (id == null) return null;
        return GetIO(id.Value);
    }
    public IIOProvider GetIO(Guid id) {
        if (!TryGetIO(id, out var io)) throw new Exception("IOProvider not found");
        return io;
    }
    public AIEngine GetAI(Guid id, string? filePrefix, string? fallBackAiPath) {
        if (!TryGetAI(id, filePrefix, out var ai, fallBackAiPath)) throw new Exception("AIProvider not found");
        return ai;
    }
    internal void MapSimpleAPI(WebApplication app) {
        if (_api != null) throw new Exception("API already mapped.");
        _api = new ServerAPIMapper(this);
        _api.MapSimpleAPI(app);
    }
}
