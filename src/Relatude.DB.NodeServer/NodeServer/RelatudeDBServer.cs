using Microsoft.AspNetCore.Hosting.Server;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.FileConversion;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer.API;
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
/// containers, event handling for store lifecycle events, and integration with I/O providers.  This class is
/// designed to be the central entry point for interacting with the Relatude database system. It includes methods for
/// starting the server, managing containers, and retrieving resources such as I/O providers.  <para> To use this
/// class, ensure that the server is properly initialized by calling <see cref="StartAsync"/>. Attempting to access
/// certain properties or methods before initialization may result in exceptions. </para></remarks>
public partial class RelatudeDBServer {
    DateTime _initialized = DateTime.UtcNow;
    public RelatudeDBServer(string? urlPath) {
        setApiUrlRoot(urlPath);
        EventHub = new ServerEventHub(this);
        EventHub.RegisterPoller(new DataStoreStatesEventPoller());
        EventHub.RegisterPoller(new DataStoreStatusEventPoller());
        EventHub.RegisterPoller(new DataStoreInfoEventPoller());
        EventHub.RegisterPoller(new DataStoreTraceEventPoller());
    }
    void setApiUrlRoot(string? urlPath) {
        if (urlPath == null) urlPath = Defaults.AdminUrlRoot;
        if (!string.IsNullOrWhiteSpace(urlPath)) ApiUrlRoot = urlPath;
        if (ApiUrlRoot.EndsWith('/')) ApiUrlRoot = ApiUrlRoot[0..^1];
        if (!ApiUrlRoot.StartsWith('/') && ApiUrlRoot.Length > 0) ApiUrlRoot = '/' + ApiUrlRoot;
        Console.WriteLine("Relatude.DB Admin UI set to: " + ApiUrlRoot);
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
    SettingsOverlay? _settingsOverlay;
    Dictionary<Guid, IIOProvider> _ios = [];
    public void ResetIOProviders() {
        lock (_ios) _ios.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public ServerOptions? Options { get; private set; }

    SimpleAuthentication? _authentication;
    public SimpleAuthentication Authentication {
        get {
            if (_authentication == null) throw new Exception("Authentication not initialized. Make sure to call RelatudeDBServer.StartAsync() before using the server.");
            return _authentication;
        }
    }
    internal string RootDataFolderPath => _rootDataFolderPath;
    internal string DefaultSubDataFolderPath => Path.Combine(_rootDataFolderPath, Defaults.DataFolderPath);
    public string ApiUrlRoot { get; private set; } = string.Empty;
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
            if (_defaultContainer?.Store == null) return new DataStoreOpeningStatus(0, 0, 0);
            if (_defaultContainer.Store.Datastore.State != DataStoreState.Opening) return new DataStoreOpeningStatus(100, 0, 0);
            return _defaultContainer.Store.Datastore.GetOpeningStatus();
        } catch (Exception err) {
            Log("Error occurred during progress estimate: " + err.Message);
            return new DataStoreOpeningStatus(0, 0, 0);
        }
    }
    public async Task StartAsync(WebApplication app, ServerOptions options) {
        Options = options;

        var dataFolderPath = options.DefaultDataFolderPath;
        var tempFolderPath = options.DefaultTempFolderPath;
        var settings = options.SettingsLoader;

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
            try { TempIO.DeleteFileIfItExists(file.Key); } catch { }
        }
        _settingsLoader = settings == null ? new LocalSettingsLoaderFile(Path.Combine(_rootDataFolderPath, _settingsFile)) : settings;
        Stopwatch sw = Stopwatch.StartNew();
        if (tempCount == 0) Log("Loading settings using: " + _settingsLoader.GetType().FullName);
        _serverSettings = await _settingsLoader.ReadAsync();
        Log("Settings loaded in " + sw.Elapsed.TotalMilliseconds.To1000N() + " ms. Found " + (_serverSettings.ContainerSettings?.Length ?? 0) + " container(s).");
        if (options.ConfigurationSectionName != null) {
            _settingsOverlay = SettingsOverlay.Create(app.Configuration, options.ConfigurationSectionName,
                Log, msg => { Log(msg); Console.Error.WriteLine("relatude.db: " + msg); });
            if (_settingsOverlay != null) _serverSettings = _settingsOverlay.Apply(_serverSettings);
        }
        if (_serverSettings.DBAdminUIUrlPath != null) setApiUrlRoot(_serverSettings.DBAdminUIUrlPath);
        RaiseEventServerSettingsInit(_serverSettings);
        if (_serverSettings.ContainerSettings != null) {
            foreach (var containerSettings in _serverSettings.ContainerSettings) {
                RaiseEventContainerSettingsInit(containerSettings);
                if(containerSettings .LocalSettings != null) RaiseEventStoreSettingsInit(containerSettings.LocalSettings, containerSettings);
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
        // Dispose the stores on host shutdown so pending work is flushed and the index engines
        // commit before the process exits. Crash-safety does not depend on this — the WAL replay
        // rebuilds anything lost — but a clean stop avoids the replay cost on the next start.
        app.Lifetime.ApplicationStopping.Register(Shutdown);
    }
    /// <summary>Disposes every database container (flushing pending writes and committing the
    /// index engines). Called automatically on host shutdown.</summary>
    public void Shutdown() {
        Log("Server shutting down, disposing databases.");
        foreach (var container in Containers.Values) {
            try { container.Dispose(); } catch (Exception err) { Log("Error disposing \"" + container.Settings.Name + "\": " + err.Message); }
        }
    }
    int _remaingToAutoOpenCount = 0;
    public bool AnyRemaingToAutoOpenIncludingFailed => Interlocked.CompareExchange(ref _remaingToAutoOpenCount, 0, 0) > 0;


    void autoOpenContainer(NodeStoreContainer container, bool throwException) {
        try {
            var sw = Stopwatch.StartNew();
            container.StartUpException = null;
            container.StartUpExceptionDateTimeUTC = null;
            Thread.Sleep(300); // give some time for the server to finish starting up
            container.Open();
            Log("Database \"" + container.Settings.Name + "\" opened in " + sw.Elapsed.TotalMilliseconds.To1000N() + " ms.");
        } catch (Exception err) {
            container.StartUpException = err;
            container.StartUpExceptionDateTimeUTC = DateTime.UtcNow;
            Log("An error occurred while opening \"" + container.Settings.Name + "\". " + err.Message);
            Console.WriteLine(err.Message);
            if (throwException) throw;
        } finally {
            Interlocked.Decrement(ref _remaingToAutoOpenCount);
        }
    }

    public void UpdateWAFServerSettingsFile() {
        _serverSettings.ContainerSettings = Containers.Values.Select(c => c.Settings).ToArray();
        var settingsToWrite = _settingsOverlay == null ? _serverSettings : _settingsOverlay.RemoveOverridesBeforeSave(_serverSettings);
        _settingsLoader!.WriteAsync(settingsToWrite).Wait();
        if (Containers.ContainsKey(_serverSettings.DefaultStoreId)) _defaultContainer = Containers[_serverSettings.DefaultStoreId];
    }
    public NodeStore GetStore(Guid storeId) {
        if (!Containers.TryGetValue(storeId, out var container)) throw new Exception("Container not found.");
        if (container.Store == null) throw new Exception("Store not initialized. ");
        return container.Store;
    }
    internal void RaiseEventServerSettingsInit( RelatudeDBServerSettings serverSettings ) {
        if (Options?.OnServerSettingsInit == null) return;
        try {
            Options.OnServerSettingsInit(serverSettings);
        } catch (Exception err) {
            Log("Error occurred during OnServerSettingsInit event: " + err.Message);
        }
    }
    internal void RaiseEventContainerSettingsInit(NodeStoreContainerSettings containerSettings) {
        if (Options?.OnContainerSettingsInit == null) return;
        try {
            Options.OnContainerSettingsInit(containerSettings);
        } catch (Exception err) {
            Log("Error occurred during OnContainerSettingsInit event: " + err.Message);
        }
    }
    internal void RaiseEventStoreSettingsInit(SettingsLocal storeSettings, NodeStoreContainerSettingsBase containerSettings) {
        if (Options?.OnStoreSettingsInit == null) return;
        try {
            Options.OnStoreSettingsInit(storeSettings, containerSettings);
        } catch (Exception err) {
            Log("Error occurred during OnStoreSettingsInit event: " + err.Message);
        }
    }
    internal void RaiseEventDatamodelInit(Datamodel datamodel, NodeStoreContainerSettingsBase containerSettings) {
        if (Options?.OnDatamodelInit == null) return;
        try {
            Options.OnDatamodelInit(datamodel, containerSettings);
        } catch (Exception err) {
            Log("Error occurred during OnDatamodelInit event: " + err.Message);
        }
    }
    internal void RaiseEventStoreInit(NodeStoreContainer nodeStoreContainer, NodeStore store) {
        if (Options?.OnStoreInit == null) return;
        try {
            Options.OnStoreInit(store);
        } catch (Exception err) {
            Log("Error occurred during OnStoreInit event: " + err.Message);
        }
    }
    internal void RaiseEventStoreOpen(NodeStoreContainer nodeStoreContainer, NodeStore store) {
        if (Options?.OnStoreOpen != null) {
            try {
                Options.OnStoreOpen(store);
            } catch (Exception err) {
                Log("Error occurred during OnStoreOpen event: " + err.Message);
            }
        }
        if (Options?.OnStoreOpenBackground != null) {
            ThreadPool.QueueUserWorkItem((_) => {
                try {
                    Options.OnStoreOpenBackground(store);
                } catch (Exception err) {
                    Log("Error occurred during OnStoreOpenBackground event: " + err.Message);
                }
            });
        }
    }
    internal void RaiseEventStoreClose(NodeStoreContainer nodeStoreContainer, NodeStore store) {
        if (Options?.OnStoreClose == null) return;
        try {
            Options.OnStoreClose(store);
        } catch (Exception err) {
            Log("Error occurred during OnStoreClose event: " + err.Message);
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
    public IIOProvider? GetOrNullIO(Guid? id) {
        if (id == null) return null;
        return GetIO(id.Value);
    }
    public IIOProvider GetIO(Guid id) {
        if (!TryGetIO(id, out var io)) throw new Exception("IOProvider not found");
        return io;
    }
    internal void MapAdminAPI(WebApplication app) {
        if (_api != null) throw new Exception("API already mapped.");
        _api = new ServerAPIMapper(this);
        _api.MapSimpleAPI(app);
    }
}
public class ServerOptions {
    public static string DefaultFileRootUrl => "/files";

    /// <summary>
    /// Gets or sets the callback that is invoked when the server settings are initialized.
    /// This is called after SettingsLoader.ReadAsync() is called, and before any containers are opened.
    /// The default settings loader is a read file named "relatude.db.json" in the root data folder, but can be overridden by setting the SettingsLoader property.
    /// It is a good place to programmatically modify the server settings before any containers are opened.
    /// </summary>
    public Action<RelatudeDBServerSettings>? OnServerSettingsInit { get; set; }

    /// <summary>
    /// Gets or sets the callback that is invoked when the container settings are initialized.
    /// This is called after SettingsLoader.ReadAsync() is called, and before any containers are opened.
    /// The default settings loader is a read file named "relatude.db.json" in the root data folder, but can be overridden by setting the SettingsLoader property.
    /// It is a good place to programmatically modify the container settings before any containers are opened.
    /// </summary>
    public Action<NodeStoreContainerSettings>? OnContainerSettingsInit { get; set; }

    /// <summary>
    /// Gets or sets the callback that is invoked when the store settings are initialized.
    /// This is called after SettingsLoader.ReadAsync() is called, and before any containers are opened.
    /// The default settings loader is a read file named "relatude.db.json" in the root data folder, but can be overridden by setting the SettingsLoader property.
    /// It is a good place to programmatically modify the store settings before any containers are opened.
    /// </summary>
    public Action<SettingsLocal, NodeStoreContainerSettingsBase>? OnStoreSettingsInit { get; set; }

    /// <summary>
    /// Gets or sets the callback that is invoked when a new datamodel is initialized.
    /// This is called every time a new datamodel is initialized, and before it is opened.
    /// This is a good place to register custom task runners and plugins.
    /// </summary>
    public Action<Datamodel, NodeStoreContainerSettingsBase>? OnDatamodelInit { get; set; }
    /// <summary>
    /// Callback that is triggered when a new database is initialized. 
    /// This is called every time a database is initialized, and before it it opened.
    /// This is a good place to register custom task runners and plugins.
    /// </summary>
    public Action<NodeStore>? OnStoreInit { get; set; }
    /// <summary>
    /// Gets or sets the callback to invoke when the database is closed.
    /// </summary>
    public Action<NodeStore>? OnStoreClose { get; set; }
    /// <summary>
    /// Gets or sets the callback that is invoked when the database is opened.
    /// </summary>
    public Action<NodeStore>? OnStoreOpen { get; set; }

    /// <summary>
    /// Gets or sets the callback that is invoked when the database is opened, but in a background thread.
    /// </summary>
    public Action<NodeStore>? OnStoreOpenBackground { get; set; }

    /// <summary>
    /// Custom storage for server settings.
    /// If not set, settings will be stored in a file named "relatude.db.json" in the root data folder.
    /// </summary>
    public ISettingsLoader? SettingsLoader { get; set; } = null;
    /// <summary>
    /// Name of the configuration section that is merged over the loaded settings, giving appsettings.json,
    /// appsettings.{Environment}.json, environment variables and user secrets the last word.
    /// The section has the same shape as relatude.db.json. Overridden values are never written back to the
    /// settings file. Set to null to disable. Defaults to "RelatudeDB".
    /// </summary>
    public string? ConfigurationSectionName { get; set; } = SettingsOverlay.DefaultSectionName;
    /// <summary>
    /// Default relative or absolute path to default data folder
    /// </summary>
    public string? DefaultDataFolderPath { get; set; } = null;
    /// <summary>
    /// Default relative or absolute path to temporary folder, used for uploads etc.
    /// </summary>
    public string? DefaultTempFolderPath { get; set; } = null;
    public List<IFileConverter> FileConverters { get; set; } = [];


}
