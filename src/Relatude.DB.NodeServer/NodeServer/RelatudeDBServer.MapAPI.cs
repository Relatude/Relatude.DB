using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using Relatude.DB.CodeGeneration;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Logging.Statistics;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer.Models;
using Relatude.DB.NodeServer.EventHub;
using Microsoft.AspNetCore.Mvc;
using Relatude.DB.Demo;
namespace Relatude.DB.NodeServer;
public partial class RelatudeDBServer {
    NodeStoreContainer container(Guid storeId) {
        if (_containers.TryGetValue(storeId, out var container)) return container;
        throw new Exception("Container not found.");
    }
    NodeStore db(Guid storeId) {
        return container(storeId).Store ?? throw new Exception("Store not initialized. ");
    }
    bool isDbInitialized(Guid storeId) => container(storeId).Store! != null;
    public void MapSimpleAPI(WebApplication app) {

        // Public API, NOT requiring authentication:
        mapRoot(app, action => ApiUrlPublic + action + "/");  // static files, index.html, css, js, favicon.ico for admin UI
        mapAuth(app, action => ApiUrlPublic + action + "/");  // authentication, login, ping, version, logout, etc.

        // Private API, requiring authentication:
        var path = (string section) => ApiUrlRoot + "/" + section + "/";
        mapStatus(app, action => path("status") + action);
        mapSettings(app, action => path("settings") + action);
        mapMaintenance(app, action => path("maintenance") + action);
        mapServer(app, action => path("server") + action);
        mapData(app, action => path("data") + action);
        //mapTasks(app, action => path("tasks") + action);
        mapDatamodel(app, action => path("datamodel") + action);
        mapLog(app, action => path("log") + action);
        mapDemo(app, action => path("demo") + action);

    }

    static string getResource(string name) {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = assembly.GetName().Name + ".";
        using var stream = assembly.GetManifestResourceStream(prefix + name);
        if (stream == null) throw new Exception("Resource not found: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    static ulong getHash(string name) => getResource(name).XXH64Hash();
    static ulong uiHash = getHash("ClientUI.index.5246294a.css") ^ getHash("ClientUI.index.30b17246.js");

    // PUBLIC API and with no authentication (controlled by urlpath in middleware):
    void mapRoot(WebApplication app, Func<string, string> path) {
        // a unique hash to ensure a new url for each new version of the client
        // but also to make sure unchanged ui is cached by the browser
        // not a secret, just a unique string so string replace works
        byte[] getBinaryResource(string name) {
            var assembly = Assembly.GetExecutingAssembly();
            var prefix = assembly.GetName().Name + ".";
            using var stream = assembly.GetManifestResourceStream(prefix + name);
            if (stream == null) throw new Exception("Resource not found: " + name);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        app.MapGet(ApiUrlRoot, (HttpContext ctx) => { // index.html
            ctx.Response.ContentType = "text/html";
            return getResource("ClientUI.index.html")
            .Replace("index.5246294a.css", path(uiHash + ".css"))
            .Replace("index.30b17246.js", path(uiHash + ".js"))
            .Replace("https://replace.me/favicon.ico", path("favicon.ico"))
            ;
        });
        app.MapGet(path(uiHash + ".css"), (HttpContext ctx) => {
            ctx.Response.ContentType = "text/css";
            ctx.Response.Headers.Append("Cache-Control", "public, max-age=315360000");
            return getResource("ClientUI.index.5246294a.css");
        });
        app.MapGet(path(uiHash + ".js"), (HttpContext ctx) => {
            ctx.Response.ContentType = "text/javascript";
            ctx.Response.Headers.Append("Cache-Control", "public, max-age=315360000");
            return getResource("ClientUI.index.30b17246.js");
        });
        app.MapPost("relatude.db-public-status", () => {
            return new {
                Starting = anyRemaingToAutoOpen,
                ProgressEstimate = getStartingProgressEstimate(),
                ResponseCheck = "Valid",
            };
        });
        app.MapGet(path("favicon.ico"), (HttpContext ctx) => {
            var data = getBinaryResource("ClientUI.Images.favicon.ico");
            return Results.File(data, "image/x-icon", "favicon.ico");
        });
    }
    public class Credentials {
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";
        public bool Remember { get; set; } = false;
    }
    void mapAuth(WebApplication app, Func<string, string> path) {
        app.MapGet(path("ping"), () => "pong");
        app.MapPost(path("ping"), () => "pong");
        app.MapPost(path("login"), (HttpContext context, Credentials credentials) => {
            if (Authentication.AreCredentialsValid(credentials.UserName, credentials.Password)) {
                Authentication.LogIn(context, credentials.Remember);
                return new { Success = true };
            }
            return new { Success = false };
        });
        app.MapPost(path("have-users"), (HttpContext context) => {
            return !string.IsNullOrEmpty(Settings.MasterUserName) && !string.IsNullOrEmpty(Settings.MasterPassword);
        });
        app.MapPost(path("is-logged-in"), (HttpContext context) => Authentication.IsLoggedIn(context));
        app.MapPost(path("version"), () => { return new { Version = "1.0.0" }; });
        app.MapPost(path("logout"), (HttpContext context) => Authentication.LogOut(context));
    }

    // PRIVATE API, requires authentication (controlled by path in middleware):
    void mapStatus(WebApplication app, Func<string, string> path) {
        app.MapPost(path("status-all"), () => _containers.Values.Select(c => new { c.Settings.Id, c.Status }));
        app.MapGet(path("events"), ServerEventHub.Subscribe);
        app.MapPost(path("change-subscription"), ServerEventHub.ChangeSubscription);
        app.MapPost(path("get-all-subscriptions"), ServerEventHub.GetAllSubscriptions);
        app.MapPost(path("get-subscription-count"), ServerEventHub.SubscriptionCount);
    }
    void mapSettings(WebApplication app, Func<string, string> path) {
        app.MapPost(path("get-settings"), (Guid storeId) => container(storeId).Settings);
        app.MapPost(path("set-settings"), (Guid storeId, [FromBody] NodeStoreContainerSettings settings) => {
            container(storeId).ApplyNewSettings(settings, true);
            updateWAFServerSettingsFile();
        });
        app.MapPost(path("re-save-settings"), (Guid storeId) => {
            updateWAFServerSettingsFile();
        });
    }
    void mapMaintenance(WebApplication app, Func<string, string> path) {
        app.MapPost(path("open"), (Guid storeId) => container(storeId).Open());
        app.MapPost(path("close"), (Guid storeId) => {
            container(storeId).CloseIfOpen();
            if (_containers.Values.Count(c => c.IsOpenOrOpening()) == 0) {
                lock (_ios) _ios.Clear();
                lock (_ais) {
                    foreach (var ai in _ais.Values) ai.Dispose();
                    _ais.Clear();
                }
            }
        });
        app.MapPost(path("reset-io-locks"), (Guid storeId, Guid ioId) => GetIO(ioId).ResetLocks());
        app.MapPost(path("get-store-files"), (Guid storeId, Guid ioId) => new FileKeyUtility(container(storeId).Settings?.LocalSettings?.FilePrefix).GetAllFiles(GetIO(ioId)));
        app.MapPost(path("file-exist"), (Guid storeId, Guid ioId, string fileName) => !GetIO(ioId).DoesNotExistOrIsEmpty(fileName));
        app.MapPost(path("backup-now"), (Guid storeId, Guid ioId, bool truncate, bool keepForever) => container(storeId).Store!.Datastore.BackUpNow(truncate, keepForever, GetIO(ioId)));
        app.MapPost(path("is-file-key-legal"), (string fileKey) => new { IsLegal = FileKeyUtility.IsFileKeyValid(fileKey) });
        app.MapPost(path("is-file-prefix-legal"), (string filePrefix) => new { IsLegal = FileKeyUtility.IsFilePrefixValid(filePrefix, out _) });
        app.MapPost(path("get-file-key-of-db"), (Guid storeId, Guid ioId) => new FileKeyUtility(container(storeId).Settings.LocalSettings!.FilePrefix).WAL_GetLatestFileKey(GetIO(ioId)));
        app.MapPost(path("get-file-key-of-db-next"), (Guid storeId, Guid ioId) => new FileKeyUtility(container(storeId).Settings.LocalSettings!.FilePrefix).WAL_NextFileKey(GetIO(ioId)));
        app.MapGet(path("download-file"), (HttpContext ctx, Guid storeId, Guid ioId, string fileName) => {
            ensurePrefix(storeId, ref fileName);
            var ioStream = GetIO(ioId).OpenRead(fileName, 0);
            var stream = new ReadStreamWrapper(ioStream);
            return Results.File(stream, MediaTypeHeaderValue.Parse("application/octet-stream").ToString(), fileName);
        });
        app.MapPost(path("delete-file"), (HttpContext ctx, Guid storeId, Guid ioId, string fileName) => {
            ensurePrefix(storeId, ref fileName);
            GetIO(ioId).DeleteIfItExists(fileName);
        });
        app.MapPost(path("can-rename-file"), (Guid storeId, Guid ioId) => new { CanRename = GetIO(ioId).CanRenameFile });
        app.MapPost(path("rename-file"), (Guid storeId, Guid ioId, string fileName, string newFileName) => {
            ensurePrefix(storeId, ref fileName);
            ensurePrefix(storeId, ref newFileName);
            GetIO(ioId).RenameFile(fileName, newFileName);
        });
        app.MapPost(path("initiate-upload"), () => { return new { Value = Guid.NewGuid().ToString() }; });
        app.MapPost(path("upload-part"), async (HttpContext ctx, Guid uploadId) => {
            using var ioStream = _tempIO!.OpenAppend(uploadId.ToString());
            using var writeStream = new WriteStreamWrapper(ioStream);
            await ctx.Request.Body.CopyToAsync(writeStream);
        });
        app.MapPost(path("cancel-upload"), (HttpContext ctx, Guid uploadId) => _tempIO!.DeleteIfItExists(uploadId.ToString()));
        app.MapPost(path("complete-upload"), (HttpContext ctx, Guid storeId, Guid ioId, Guid uploadId, string fileName, bool overwrite) => {
            if (_tempIO!.DoesNotExistsOrIsEmpty(uploadId.ToString())) throw new Exception("Upload not found");
            var destIo = GetIO(ioId);
            ensurePrefix(storeId, ref fileName);
            var fileKeys = new FileKeyUtility(_containers[storeId].Settings?.LocalSettings?.FilePrefix);
            if (fileKeys.StateFileKey.ToLower() == fileName.ToLower()) {
                _tempIO!.DeleteIfItExists(uploadId.ToString());
                throw new Exception("Uploading state file is not allowed. ");
            }
            destIo.DeleteIfItExists(fileKeys.StateFileKey); // delete the state file to avoid old statefile and newer log file!
            if (destIo is IODisk diskIO && _tempIO is IODisk tempDiskIO) {
                diskIO.MoveFile(tempDiskIO, uploadId.ToString(), fileName, overwrite);
                return;
            }
            using var ioSourceStream = _tempIO!.OpenRead(uploadId.ToString(), 0);
            if (!destIo.DoesNotExistsOrIsEmpty(fileName) && !overwrite) throw new Exception("File already exists");
            destIo.DeleteIfItExists(fileName);
            using var ioDestStream = destIo.OpenAppend(fileName);
            using var readStream = new ReadStreamWrapper(ioSourceStream);
            using var writeStream = new WriteStreamWrapper(ioDestStream);
            readStream.CopyTo(writeStream);
            ioSourceStream.Dispose();
            _tempIO.DeleteIfItExists(uploadId.ToString());
        });
        app.MapPost(path("truncate-log"), (HttpContext ctx, Guid storeId) => db(storeId).MaintenanceAsync(MaintenanceAction.TruncateLog));
        app.MapPost(path("save-index-states"), (HttpContext ctx, Guid storeId) => db(storeId).MaintenanceAsync(MaintenanceAction.SaveIndexStates));
        app.MapPost(path("clear-cache"), (HttpContext ctx, Guid storeId) => db(storeId).Datastore.MaintenanceAsync(MaintenanceAction.ClearCache | MaintenanceAction.GarbageCollect));
        app.MapPost(path("info"), async (HttpContext ctx, Guid storeId) => {
            var store = container(storeId).Store;
            if (store == null) return null;
            return await store.Datastore.GetInfoAsync();
        });
        app.MapPost(path("clean-temp-files"), () => _tempIO!.GetFiles().ForEach(file => { try { _tempIO!.DeleteIfItExists(file.Key); } catch { } }));
        app.MapPost(path("get-size-temp-files"), () => new { TotalSize = _tempIO!.GetFiles().Sum(file => file.Size) });
        app.MapGet(path("download-truncated-db"), (Guid storeId) => {
            var fileKey = Guid.NewGuid().ToString();
            db(storeId).Datastore.RewriteStore(false, fileKey, _tempIO);
            var ioStream = _tempIO!.OpenRead(fileKey, 0);
            var stream = new ReadStreamWrapper(ioStream);
            var name = container(storeId).Settings.Name;
            if (string.IsNullOrEmpty(name)) name = "Database";
            var fileName = name + " " + DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ss") + ".bin";
            //var fileName = datastore.FileKeys.Log_NextFileKey(datastore.IO);
            return Results.File(stream, MediaTypeHeaderValue.Parse("application/octet-stream").ToString(), fileName);
        });
        app.MapGet(path("download-full-db"), (Guid storeId) => {
            var fileKey = Guid.NewGuid().ToString();
            var datastore = container(storeId).Store!.Datastore;
            datastore.CopyStore(fileKey, _tempIO);
            var ioStream = _tempIO!.OpenRead(fileKey, 0);
            var stream = new ReadStreamWrapper(ioStream);
            var name = container(storeId).Settings.Name;
            if (string.IsNullOrEmpty(name)) name = "Database";
            var fileName = name + " " + DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ss") + ".bin";
            //var fileName = datastore.FileKeys.Log_NextFileKey(datastore.IO);
            return Results.File(stream, MediaTypeHeaderValue.Parse("application/octet-stream").ToString(), fileName);
        });
        app.MapPost(path("copy-file"), (Guid storeId, Guid fromIoId, string fromFileName, Guid toIoId, string toIoFileName) => {
            ensurePrefix(storeId, ref fromFileName);
            ensurePrefix(storeId, ref toIoFileName);
            var io = GetIO(toIoId);
            io.CopyFile(fromFileName, toIoFileName);
        });
        app.MapPost(path("delete-all-but-db"), (Guid storeId) => {
            var settings = container(storeId).Settings;
            if (settings.LocalSettings == null) throw new Exception("LocalSettings is required for NodeStoreContainerSettings");
            var fileKeys = new FileKeyUtility(settings.LocalSettings.FilePrefix);
            IIOProvider io;
            if (!settings.IoDatabase.HasValue || settings.IoDatabase == Guid.Empty) throw new Exception("IoDatabase is required for NodeStoreContainerSettings");
            io = GetIO(settings.IoDatabase.Value);
            var dbFile = fileKeys.WAL_GetLatestFileKey(io);
            var fileStore = fileKeys.FileStore_GetLatestFileKey(io);
            var indexFile = fileKeys.StateFileKey;
            foreach (var file in fileKeys.GetAllFiles(io)) {
                if (file.Writers > 0 || file.Readers > 0) continue;
                if (indexFile == file.Key) continue;
                if (dbFile == file.Key) continue;
                if (fileStore == file.Key) continue;
                if (fileKeys.WAL_KeepForever(file.Key)) continue;
                io.DeleteIfItExists(file.Key);
            }
        });
        app.MapPost(path("delete-all-files"), (Guid storeId, Guid ioId) => {
            var settings = container(storeId).Settings;
            if (settings.LocalSettings == null) throw new Exception("LocalSettings is required for NodeStoreContainerSettings");
            var fileKeys = new FileKeyUtility(settings.LocalSettings.FilePrefix);
            var io = GetIO(ioId);
            var dbFile = fileKeys.WAL_GetLatestFileKey(io);
            var fileStore = fileKeys.FileStore_GetLatestFileKey(io);
            var indexFile = fileKeys.StateFileKey;
            foreach (var file in fileKeys.GetAllFiles(io)) {
                if (file.Writers > 0 || file.Readers > 0) continue;
                io.DeleteIfItExists(file.Key);
            }
        });
    }
    void mapServer(WebApplication app, Func<string, string> path) {
        app.MapPost(path("get-store-containers"), () => {
            return _containers.Values.Select(c => new {
                c.Settings.Id,
                c.Settings.Name,
                c.Settings.Description,
                c.Status,
                c.Settings.IoDatabase,
            });
        });
        app.MapPost(path("get-default-store-id"), () => _serverSettings.DefaultStoreId.ToString());
        app.MapPost(path("set-default-store-id"), (Guid storeId) => {
            _serverSettings.DefaultStoreId = storeId;
            updateWAFServerSettingsFile();
        });
        app.MapPost(path("set-master-credentials"), ([FromBody] dynamic settings) => {
            _serverSettings.MasterUserName = settings.MasterUserName;
            _serverSettings.MasterPassword = settings.MasterPassword;
            updateWAFServerSettingsFile();
        });
        app.MapPost(path("set-name-and-description"), ([FromBody] dynamic settings) => {
            _serverSettings.Name = settings.Name;
            _serverSettings.Description = settings.Description;
            updateWAFServerSettingsFile();
        });
        app.MapPost(path("create-store"), () => {
            var id = Guid.NewGuid();
            var containerSettings = new NodeStoreContainerSettings() { Id = id, Name = "New Store" };
            var container = new NodeStoreContainer(containerSettings, this);
            _containers.Add(id, container);
            updateWAFServerSettingsFile();
            return containerSettings;
        });
        app.MapPost(path("remove-store"), (Guid storeId) => {
            container(storeId).CloseIfOpen();
            _containers.Remove(storeId);
            updateWAFServerSettingsFile();
        });
        app.MapPost(path("get-server-log"), () => getStartUpLog().Select(e => { return new { Timestamp = e.Item1, Description = e.Item2 }; }).ToArray());
        app.MapPost(path("clear-server-log"), clearStartUpLog);
    }
    void mapData(WebApplication app, Func<string, string> path) {
        app.MapPost(path("queue-re-index-all"), (Guid storeId) => {
            var allIds = db(storeId).Query<object>().SelectId().Execute();
            var transaction = db(storeId).CreateTransaction();
            foreach (var id in allIds) transaction.ReIndex(id);
            ThreadPool.QueueUserWorkItem(_ => { transaction.Execute(); });
            return allIds.Count;
        });
        app.MapPost(path("query"), (Guid storeId, QueryModel query) => GetStore(storeId).EvaluateForJsonAsync(query.Query, [.. query.Parameters.Select(ParameterModel.Convert)]));
        app.MapPost(path("execute"), (Guid storeId, ActionModel[] actions, bool flushToDisk) => GetStore(storeId).ExecuteAsync(actions, flushToDisk));
        app.MapPost(path("shift-all-dates"), async (Guid storeId, int seconds) => {
            var store = container(storeId).Store;
            if (store == null) throw new Exception("Store not initialized. ");
            var transaction = store.CreateTransaction();
            var dm = store.Datastore.Datamodel;
            var propsByNodeType = dm.Properties.Values
                .Where(p => p.PropertyType == PropertyType.DateTime)
                .GroupBy(p => p.NodeType)
                .Select(g => new { NodeTypeId = g.Key, Properties = g });
            var shift = TimeSpan.FromSeconds(seconds);
            foreach (var g in propsByNodeType) {
                var nodeIds = store.QueryType(g.NodeTypeId).SelectId().Execute();
                foreach (var property in g.Properties) {
                    foreach (var id in nodeIds) {
                        transaction.AddToProperty(id, property.Id, shift);
                    }
                }
            }
            await transaction.ExecuteAsync();
            return transaction.Count;
        });
    }
    void mapDatamodel(WebApplication app, Func<string, string> path) {
        app.MapPost(path("get-code"), (Guid storeId, bool addAttributes) => CodeGeneratorForCSharpModels.GenerateCSharpModelCode(db(storeId).Datastore.Datamodel, addAttributes));
        //app.MapPost(path("get-model"), (Guid storeId, Guid datamodelId) => db(storeId).Datastore.Datamodel);
        app.MapPost(path("get-model"), (Guid storeId) => db(storeId).Datastore.Datamodel);
        app.MapPost(path("server"), (Guid storeId, Guid datamodelId) => db(storeId).Datastore.Datamodel);
    }
    void mapLog(WebApplication app, Func<string, string> path) {
        var logger = (Guid storeId) => container(storeId).GetLogger();
        app.MapPost(path("has-startup-exception"), (Guid storeId) => container(storeId).StartUpException != null);
        app.MapPost(path("get-startup-exception"), (Guid storeId) => {
            var c = container(storeId);
            var e = c.StartUpException;
            if (e == null) return null;
            return new { When = c.StartUpExceptionDateTimeUTC, e.Message, e.StackTrace, };
        });
        app.MapPost(path("get-log-infos"), (Guid storeId) => {
            var loggerInstance = logger(storeId);
            var keysAndNames = loggerInstance.GetLogKeysAndNames();
            return keysAndNames.Select(k => new {
                k.Key,
                Name = k.Value,
                EnabledLog = loggerInstance.IsLogEnabled(k.Key),
                EnabledStatistics = loggerInstance.IsStatisticsEnabled(k.Key),
                FirstRecord = loggerInstance.LogStore.GetTimestampOfFirstRecord(k.Key),
                LastRecord = loggerInstance.LogStore.GetTimestampOfLastRecord(k.Key),
                TotalFileSize = loggerInstance.LogStore.GetFileSize(k.Key),
                LogFileSize = loggerInstance.LogStore.GetLogFileSize(k.Key),
                StatisticsFileSize = loggerInstance.LogStore.GetStatisticsFileSize(k.Key),
            });
        });
        app.MapPost(path("enable-log"), (Guid storeId, string logKey, bool enable) => logger(storeId).EnableLog(logKey, enable));
        app.MapPost(path("is-log-enabled"), (Guid storeId, string logKey) => logger(storeId).IsLogEnabled(logKey));
        app.MapPost(path("enable-statistics"), (Guid storeId, string logKey, bool enable) => logger(storeId).EnableStatistics(logKey, enable));
        app.MapPost(path("is-statistics-enabled"), (Guid storeId, string logKey) => logger(storeId).IsStatisticsEnabled(logKey));
        app.MapPost(path("clear-log"), (Guid storeId, string logKey) => logger(storeId).ClearLog(logKey));
        app.MapPost(path("clear-statistics"), (Guid storeId, string logKey) => logger(storeId).ClearStatistics(logKey));
        app.MapPost(path("extract-log"), (Guid storeId, string logKey, DateTime from, DateTime to, int skip, int take, bool orderByDescendingDates) => logger(storeId).ExtractLog(logKey, from, to, skip, take, orderByDescendingDates, out var total));

        app.MapPost(path("set-property-hits-recording-status"), (Guid storeId, bool enabled) => logger(storeId).RecordingPropertyHits = enabled);
        app.MapPost(path("is-recording-property-hits"), (Guid storeId) => logger(storeId).RecordingPropertyHits);
        app.MapPost(path("analyze-property-hits"), (Guid storeId) => logger(storeId).AnalyzePropertyHits().Select(kv => new { PropertyName = kv.Key, HitCount = kv.Value }));

        app.MapPost(path("analyze-system-log-count"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseSystemLogCount(intervalType, from, to));
        app.MapPost(path("analyze-system-log-count-by-type"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseSystemLogCountByType(intervalType, from, to));
        app.MapPost(path("analyze-query-count"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseQueryCount(intervalType, from, to));
        app.MapPost(path("analyze-query-duration"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseQueryDuration(intervalType, from, to));
        app.MapPost(path("analyze-transaction-count"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseTransactionCount(intervalType, from, to));
        app.MapPost(path("analyze-transaction-duration"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseTransactionDuration(intervalType, from, to));
        app.MapPost(path("analyze-transaction-action"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseTransactionAction(intervalType, from, to));
        app.MapPost(path("analyze-action-count"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseActionCount(intervalType, from, to));
        app.MapPost(path("analyze-action-operations"), (Guid storeId, IntervalType intervalType, DateTime from, DateTime to) => logger(storeId).AnalyseActionOperations(intervalType, from, to));

    }
    void mapTasks(WebApplication app, Func<string, string> path) {
        app.MapPost(path("get-batch-count-queued"), (Guid storeId) => db(storeId).Datastore.TaskQueue.CountBatch(Tasks.BatchState.Pending));
        app.MapPost(path("get-batch-count-per-state"), (Guid storeId) => db(storeId).Datastore.TaskQueue.BatchCountsPerState());
        app.MapPost(path("get-batch-info"), (Guid storeId, Tasks.BatchState[] states, string[] typeIds, string[] jobIds, int page, int pageSize) => {
            return db(storeId).Datastore.TaskQueue.GetBatchMeta(states, typeIds, jobIds, page, pageSize, out var totalCount);
        });
        app.MapPost(path("set-batch-state"), (Guid storeId, Guid[] batchIds, Tasks.BatchState state) => {
            db(storeId).Datastore.TaskQueue.SetState(batchIds, state);
        });
        app.MapPost(path("delete-batch-by-id"), (Guid storeId, Guid[] batchIds) => {
            db(storeId).Datastore.TaskQueue.DeleteById(batchIds);
        });
    }
    void mapDemo(WebApplication app, Func<string, string> path) {
        app.MapPost(path("populate"), (Guid storeId, int count) => {
            var store = db(storeId);
            var sw = new Stopwatch();
            var chunkSize = 10000;
            var created = 0;
            var generator = new DemoArticleGenerator();
            while (true) {
                var create = Math.Min(chunkSize, count - created);
                if (create <= 0) break;
                var articles = generator.Many(create);
                sw.Start();
                store.Insert(articles);
                sw.Stop();
                created += create;
            }
            sw.Start();
            store.Flush();
            sw.Stop();
            return new {
                CountCreated = count,
                ElapsedMs = sw.Elapsed.TotalMilliseconds
            };
        });
    }
}
