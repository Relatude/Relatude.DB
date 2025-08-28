using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using WAF.AI;
using WAF.Common;
using WAF.IO;
using WAF.Nodes;
using WAF.Tasks;
namespace WAF.NodeServer;
public static partial class WAFServer {
    readonly static List<Tuple<DateTime, string>> _serverLog = [];
    static void startUpLog(string msg) { lock (_serverLog) { _serverLog.Add(new(DateTime.UtcNow, msg)); } }
    static Tuple<DateTime, string>[] getStartUpLog() { lock (_serverLog) { return _serverLog.ToArray(); } }
    static void clearStartUpLog() { lock (_serverLog) { _serverLog.Clear(); } }
    static string _settingsFile = "waf.settings.json";
    static string _rootDataFolderPath = string.Empty;
    static IIOProvider? _tempIO;
    static ISettingsLoader? _settingsLoader;
    static Dictionary<Guid, IIOProvider> _ios = [];
    static Dictionary<string, IAIProvider> _ais = [];

    public static event EventHandler<NodeStore>? OnStoreInit;
    public static event EventHandler<NodeStore>? OnStoreOpen;
    public static event EventHandler<NodeStore>? OnStoreDispose;

    internal static string ApiUrlRoot { get; private set; } = string.Empty;
    internal static string ApiUrlPublic => ApiUrlRoot + "/auth/";
    static WAFServerSettings _serverSettings = new() { Id = Guid.NewGuid(), Name = "WAF Server" };
    public static WAFServerSettings Settings { get { return _serverSettings; } }
    static Dictionary<Guid, NodeStoreContainer> _containers = [];
    static NodeStoreContainer[] _containersToAutoOpen = [];
    static NodeStoreContainer? _defaultContainer = null;
    public static bool DefaultStoreIsOpen() => _defaultContainer != null && _defaultContainer.IsOpen();
    public static NodeStoreContainer? DefaultContainer => _defaultContainer;
    public static NodeStore DefaultStore => GetDefaultStoreAndWaitIfOpening();
    public static NodeStore GetDefaultStoreAndWaitIfOpening(int timeoutSec = 120) {
        if (_defaultContainer == null) throw new Exception("No default store container found or initialized. ");
        return GetStoreAndWaitIfOpening(_defaultContainer.Settings.Id, timeoutSec);
    }
    public static NodeStore GetStoreAndWaitIfOpening(Guid storeId, int timeoutSec = 12000) {
        if (!_containers.TryGetValue(storeId, out var container)) throw new Exception("Container not found.");
        var sw = Stopwatch.StartNew();
        if (container.Settings.AutoOpen) { // if auto open is enabled, we have to wait for first initialization, otherwise .datastore will be null
            while (true) {
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
            if (datastore.State != DataStoreState.Opening && container.Store != null) return container.Store;
            if (sw.Elapsed.TotalSeconds > timeoutSec) throw new Exception("Timeout waiting for store to open.");
            Thread.Sleep(100);
        }
    }

    internal static IEndpointRouteBuilder UseWAFDB(this WebApplication app, string? urlPath = "/waf",
        string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        return UseWAFDBAsync(app, urlPath, dataFolderPath, tempFolderPath, settingsIO).Result;
    }
    internal static async Task<IEndpointRouteBuilder> UseWAFDBAsync(WebApplication app, string? urlPath = "/waf",
        string? dataFolderPath = null, string? tempFolderPath = null, ISettingsLoader? settingsIO = null) {
        app.Use(progressResponse);
        app.Use(SimpleAuthentication.Authorize);
        if (!string.IsNullOrWhiteSpace(urlPath)) ApiUrlRoot = urlPath;
        if (ApiUrlRoot.EndsWith('/')) ApiUrlRoot = ApiUrlRoot[0..^1];
        if (!ApiUrlRoot.StartsWith('/') && ApiUrlRoot.Length > 0) ApiUrlRoot = '/' + ApiUrlRoot;
        await startServerAsync(app, dataFolderPath, tempFolderPath, settingsIO);
        mapAPI(app);
        return app;
    }

    static int getStartingProgressEstimate() {
        try {
            var combinedProgress = _containersToAutoOpen.Sum(c => {
                if (c.datastore == null) return 0;
                var activity = c.datastore.GetActivity();
                if (activity == null || activity.Category != DataStoreActivityCategory.Opening) return 100;
                var progress = activity.PercentageProgress;
                if (!progress.HasValue) return 0;
                return progress.Value;
            });
            return (int)Math.Ceiling((double)combinedProgress / _containersToAutoOpen.Length);
        } catch (Exception err) {
            startUpLog("Error occurred during progress estimate: " + err.Message);
            return 0;
        }
    }
    static async Task progressResponse(HttpContext ctx, Func<Task> next) {
        if (anyRemaingToAutoOpen && ctx.Request.Path == "/") {
            ctx.Response.ContentType = "text/html";
            var html = getResource("ClientStart.start.html");
            await ctx.Response.WriteAsync(html);
        } else {
            await next();
        }
    }
    public class Credentials {
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";
        public bool Remember { get; set; } = false;
    }
    static async Task startServerAsync(WebApplication app, string? dataFolderPath, string? tempFolderPath = null, ISettingsLoader? settings = null) {
        _serverLog.Clear();
        startUpLog("WAF Server started");
        var environmentRoot = app.Environment.ContentRootPath;
        if (string.IsNullOrEmpty(dataFolderPath)) dataFolderPath = string.Empty;
        dataFolderPath = dataFolderPath.EnsureDirectorySeparatorChar();
        if (!System.IO.Path.IsPathRooted(dataFolderPath)) dataFolderPath = environmentRoot.SuperPathCombine(dataFolderPath);
        _rootDataFolderPath = dataFolderPath;

        if (tempFolderPath == null) tempFolderPath = "waf.temp";
        if (!Path.IsPathRooted(tempFolderPath)) tempFolderPath = environmentRoot.SuperPathCombine(tempFolderPath);
        _tempIO = new IODisk(tempFolderPath);
        foreach (var file in _tempIO.GetFiles()) {
            try { _tempIO.DeleteIfItExists(file.Key); } catch { }
        }
        _settingsLoader = settings == null ? new LocalSettingsLoaderFile(Path.Combine(_rootDataFolderPath, _settingsFile)) : settings;



        _serverSettings = await _settingsLoader.ReadAsync();
        if (_serverSettings.ContainerSettings != null) {
            foreach (var containerSettings in _serverSettings.ContainerSettings) {
                var container = new NodeStoreContainer(containerSettings);
                _containers.Add(containerSettings.Id, container);
                if (containerSettings.Id == _serverSettings.DefaultStoreId) _defaultContainer = container;
            }
        }
        _containersToAutoOpen = _containers.Values.Where(c => c.Settings.AutoOpen).ToArray();
        _remaingToAutoOpenCount = _containersToAutoOpen.Length;
        foreach (var container in _containersToAutoOpen) {
            if (container.Settings.WaitUntilOpen) {
                openContainer(container, true);
            } else {
                ThreadPool.QueueUserWorkItem(openContainerNoException, container, true);
            }
        }
    }
    static Dictionary<Guid, List<ITaskRunner>> _runnersByContainer = [];
    public static void RegisterTaskRunner(ITaskRunner runner) {
        RegisterTaskRunner(Guid.Empty, runner); // meaning for all containers
    }
    public static void RegisterTaskRunner(Guid containerId, ITaskRunner runner) {
        lock (_runnersByContainer) {
            if (!_runnersByContainer.TryGetValue(containerId, out var runners)) {
                runners = [];
                _runnersByContainer[containerId] = runners;
            }
            runners.Add(runner);
        }
    }
    internal static IEnumerable<ITaskRunner> GetRegisteredTaskRunners(NodeStoreContainer container) {
        lock (_runnersByContainer) {
            List< ITaskRunner> values = [];
            if (_runnersByContainer.TryGetValue(container.Settings.Id, out var runners)) values.AddRange(runners);
            if (_runnersByContainer.TryGetValue(Guid.Empty, out var allRunners)) values.AddRange(allRunners);
            return values;
        }
    }
    static int _remaingToAutoOpenCount = 0;
    static bool anyRemaingToAutoOpen => Interlocked.CompareExchange(ref _remaingToAutoOpenCount, 0, 0) > 0;
    static void openContainerNoException(NodeStoreContainer container) => openContainer(container, false);
    static void openContainer(NodeStoreContainer container, bool throwException) {
        try {
            container.Open(true);
        } catch (Exception err) {
            startUpLog("An error occurred while opening \"" + container.Settings.Name + "\". " + err.Message);
            Console.WriteLine(err.Message); //
            if (throwException) throw;
        } finally {
            Interlocked.Decrement(ref _remaingToAutoOpenCount);
        }
    }
    static void updateWAFServerSettingsFile() {
        _serverSettings.ContainerSettings = _containers.Values.Select(c => c.Settings).ToArray();
        _settingsLoader!.WriteAsync(_serverSettings).Wait();
        if (_containers.ContainsKey(_serverSettings.DefaultStoreId)) _defaultContainer = _containers[_serverSettings.DefaultStoreId];
    }
    static void ensurePrefix(Guid storeId, ref string fileKey) {
        var filePrefix = _containers[storeId].Settings?.LocalSettings?.FilePrefix;
        if (string.IsNullOrEmpty(filePrefix)) return;
        if (!fileKey.StartsWith('.')) filePrefix += ".";
        if (fileKey.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase)) return;
        fileKey = filePrefix + fileKey;
    }
    static NodeStore GetStore(Guid storeId) {
        if (!_containers.TryGetValue(storeId, out var container)) throw new Exception("Container not found.");
        if (container.Store == null) throw new Exception("Store not initialized. ");
        return container.Store;
    }
    internal static void RaiseEventStoreOpen(NodeStoreContainer nodeStoreContainer, NodeStore store) {
        if (nodeStoreContainer == null) return;
        try {
            OnStoreOpen?.Invoke(nodeStoreContainer, store);
        } catch (Exception err) {
            startUpLog("Error occurred during OnStoreOpen event: " + err.Message);
        }
    }
    internal static void RaiseEventStoreInit(NodeStoreContainer nodeStoreContainer, NodeStore store) {
        if (nodeStoreContainer == null) return;
        try {
            OnStoreInit?.Invoke(nodeStoreContainer, store);
        } catch (Exception err) {
            startUpLog("Error occurred during OnStoreInit event: " + err.Message);
        }
    }
    internal static void RaiseEventStoreDispose(NodeStoreContainer nodeStoreContainer, NodeStore store) {
        if (nodeStoreContainer == null) return;
        try {
            OnStoreDispose?.Invoke(nodeStoreContainer, store);
        } catch (Exception err) {
            startUpLog("Error occurred during OnStoreDispose event: " + err.Message);
        }
    }

    public static bool TryGetIO(Guid ioId, [MaybeNullWhen(false)] out IIOProvider io) {
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
    public static bool TryGetAI(Guid id, string? filePrefix, [MaybeNullWhen(false)] out IAIProvider ai, Action<string> log) {
        lock (_ais) {
            if (_ais.TryGetValue(id + filePrefix, out ai)) return true;
            var settings = _serverSettings.AISettings?.FirstOrDefault(s => s.Id == id);
            if (settings == null) return false;
            string? folderPath = null;

            if (!string.IsNullOrEmpty(settings.FilePath)) {
                folderPath = _rootDataFolderPath.SuperPathCombine(settings.FilePath);
                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
            } else {
                throw new Exception($"AIProvider {settings.Name} [{id}] does not have a valid file path set.");
            }
            ai = AISettings.Create(settings, folderPath, filePrefix, log);
            try {
                _ais.Add(id + filePrefix, ai);
            } catch (Exception ex) {
                var msg = $"Failed to create AIProvider {settings.Name} [{id}]: {ex.Message}";
                throw new Exception(msg, ex);
            }
            return _ais.TryGetValue(id + filePrefix, out ai);
        }
    }
    public static IIOProvider GetIO(Guid id) {
        if (!TryGetIO(id, out var io)) throw new Exception("IOProvider not found");
        return io;
    }
    public static IAIProvider GetAI(Guid id, string? filePrefix, Action<string> log) {
        if (!TryGetAI(id, filePrefix, out var ai, log)) throw new Exception("AIProvider not found");
        return ai;
    }
}
