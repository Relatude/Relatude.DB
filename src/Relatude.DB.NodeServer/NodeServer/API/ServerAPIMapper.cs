using Microsoft.AspNetCore.Mvc;
using Relatude.DB.CodeGeneration;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.Demo;
using Relatude.DB.IO;
using Relatude.DB.Logging.Statistics;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer.Models;
using Relatude.DB.NodeServer.Settings;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime;
using System.Text.Json;
namespace Relatude.DB.NodeServer;

public partial class ServerAPIMapper(RelatudeDBServer server) {
    string ApiUrlPublic => server.ApiUrlPublic;
    string ApiUrlRoot => server.ApiUrlRoot;
    void ensurePrefix(Guid storeId, ref string fileKey) {
        var filePrefix = server.Containers[storeId].Settings?.LocalSettings?.FilePrefix;
        if (string.IsNullOrEmpty(filePrefix)) return;
        if (!fileKey.StartsWith('.')) filePrefix += ".";
        if (fileKey.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase)) return;
        fileKey = filePrefix + fileKey;
    }
    NodeStoreContainer container(Guid storeId) {
        if (server.Containers.TryGetValue(storeId, out var container)) return container;
        throw new Exception("Container not found.");
    }
    NodeStore db(Guid storeId) {
        return container(storeId).Store ?? throw new Exception("Store not initialized. ");
    }
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

    public static string GetResource(string name) {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = assembly.GetName().Name + ".";
        using var stream = assembly.GetManifestResourceStream(prefix + name);
        if (stream == null) throw new Exception("Resource not found: " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    static ulong getHash(string name) => GetResource(name).XXH64Hash();
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
            return GetResource("ClientUI.index.html")
            .Replace("index.5246294a.css", path(uiHash + ".css"))
            .Replace("index.30b17246.js", path(uiHash + ".js"))
            .Replace("https://replace.me/favicon.ico", path("favicon.ico"))
            ;
        });
        app.MapGet(path(uiHash + ".css"), (HttpContext ctx) => {
            ctx.Response.ContentType = "text/css";
            ctx.Response.Headers.Append("Cache-Control", "public, max-age=315360000");
            return GetResource("ClientUI.index.5246294a.css");
        });
        app.MapGet(path(uiHash + ".js"), (HttpContext ctx) => {
            ctx.Response.ContentType = "text/javascript";
            ctx.Response.Headers.Append("Cache-Control", "public, max-age=315360000");
            return GetResource("ClientUI.index.30b17246.js");
        });
        app.MapPost("relatude.db-public-status", () => {
            return new {
                Starting = server.AnyRemaingToAutoOpen,
                ProgressEstimate = server.GetStartingProgressEstimate(),
                ResponseCheck = "Valid",
            };
        });
        app.MapGet(path("favicon.ico"), (HttpContext ctx) => {
            var data = getBinaryResource("ClientUI.Images.favicon.ico");
            return Results.File(data, "image/x-icon", "favicon.ico");
        });
    }
    class Credentials {
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";
        public bool Remember { get; set; } = false;
    }
    void mapAuth(WebApplication app, Func<string, string> path) {
        app.MapGet(path("ping"), () => "pong");
        app.MapPost(path("ping"), () => "pong");
        app.MapPost(path("login"), (HttpContext context, Credentials credentials) => {
            if (server.Authentication.AreCredentialsValid(credentials.UserName, credentials.Password)) {
                server.Authentication.LogIn(context, credentials.Remember);
                return new { Success = true };
            }
            return new { Success = false };
        });
        app.MapPost(path("have-users"), (HttpContext context) => {
            return !string.IsNullOrEmpty(server.Settings.MasterUserName) && !string.IsNullOrEmpty(server.Settings.MasterPassword);
        });
        app.MapPost(path("is-logged-in"), (HttpContext context) => server.Authentication.IsLoggedIn(context));
        app.MapPost(path("version"), () => { return new { Version = "1.0.0" }; });
        app.MapPost(path("logout"), (HttpContext context) => server.Authentication.LogOut(context));
    }

    // PRIVATE API, requires authentication (controlled by path in middleware):
    void mapStatus(WebApplication app, Func<string, string> path) {
        app.MapGet(path("connect"), server.EventHub.Connect);
        app.MapPost(path("subscribe"), server.EventHub.Subscribe);
        app.MapPost(path("unsubscribe"), server.EventHub.Unsubscribe);
    }
    void mapSettings(WebApplication app, Func<string, string> path) {
        app.MapPost(path("get-settings"), (Guid storeId, bool prettyJson) => {
            return Results.Json(container(storeId).Settings, prettyJson ? LocalSettingsLoaderFile.PrettyJsonOptions : null);
        });
        app.MapPost(path("set-settings"), async (Guid storeId, HttpRequest request) => {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            var settings = JsonSerializer.Deserialize<NodeStoreContainerSettings>(body, LocalSettingsLoaderFile.PrettyJsonOptions);
            if (settings == null) throw new Exception("Invalid settings data. ");
            container(storeId).ApplyNewSettings(settings, true);
            server.UpdateWAFServerSettingsFile();
        });
        app.MapPost(path("re-save-settings"), (Guid storeId) => {
            server.UpdateWAFServerSettingsFile();
        });
    }
    void mapMaintenance(WebApplication app, Func<string, string> path) {
        app.MapPost(path("open"), (Guid storeId) => container(storeId).Open());
        app.MapPost(path("close"), (Guid storeId) => {
            container(storeId).CloseIfOpen();
            if (!server.Containers.Values.Any(c => c.IsOpenOrOpening())) server.ResetIOAndAIProviders();
        });
        app.MapPost(path("cancel-opening"), (Guid storeId) => {
            try {
                container(storeId).CloseIfOpen();
                if (!server.Containers.Values.Any(c => c.IsOpenOrOpening())) server.ResetIOAndAIProviders();
            } catch { }
        });
        app.MapPost(path("close-all-open-streams"), (Guid storeId, Guid ioId) => server.GetIO(ioId).CloseAllOpenStreams());
        app.MapPost(path("get-store-files"), (Guid storeId, Guid ioId) => new FileKeyUtility(container(storeId).Settings?.LocalSettings?.FilePrefix).GetAllFiles(server.GetIO(ioId)));
        app.MapPost(path("can-have-folders"), (Guid storeId, Guid ioId) => new { CanHave = server.GetIO(ioId).CanHaveFolders });
        app.MapPost(path("get-folders"), (Guid storeId, Guid ioId) => server.GetIO(ioId).GetFoldersAsync());
        app.MapPost(path("delete-folder"), (Guid storeId, Guid ioId, string folderName) => server.GetIO(ioId).DeleteFolderIfItExists(folderName));
        app.MapPost(path("file-exist"), (Guid storeId, Guid ioId, string fileName) => !server.GetIO(ioId).DoesNotExistOrIsEmpty(fileName));
        app.MapPost(path("backup-now"), (Guid storeId, Guid ioId, bool truncate, bool keepForever) => db(storeId).Datastore.BackUpNow(truncate, keepForever, server.GetIO(ioId)));
        app.MapPost(path("is-file-key-legal"), (string fileKey) => new { IsLegal = FileKeyUtility.IsFileKeyValid(fileKey) });
        app.MapPost(path("is-file-prefix-legal"), (string filePrefix) => new { IsLegal = FileKeyUtility.IsFilePrefixValid(filePrefix, out _) });
        app.MapPost(path("get-file-key-of-db"), (Guid storeId, Guid ioId) => {
            var settings = container(storeId).Settings;
            if (settings.IoDatabase != ioId) return string.Empty;
            return new FileKeyUtility(settings.LocalSettings!.FilePrefix).WAL_GetLatestFileKey(server.GetIO(ioId));
        });
        app.MapPost(path("get-file-key-of-db-next"), (Guid storeId, Guid ioId) => {
            var settings = container(storeId).Settings;
            if (settings.IoDatabase != ioId) return string.Empty;
            return new FileKeyUtility(settings.LocalSettings!.FilePrefix).WAL_NextFileKey(server.GetIO(ioId));
        });
        app.MapGet(path("download-file"), (HttpContext ctx, Guid storeId, Guid ioId, string fileName) => {
            ensurePrefix(storeId, ref fileName);
            var ioStream = server.GetIO(ioId).OpenRead(fileName, 0);
            var stream = new ReadStreamWrapper(ioStream);
            return Results.File(stream, MediaTypeHeaderValue.Parse("application/octet-stream").ToString(), fileName, null, null, true);
        });
        app.MapPost(path("delete-file"), (HttpContext ctx, Guid storeId, Guid ioId, string fileName) => {
            ensurePrefix(storeId, ref fileName);
            server.GetIO(ioId).DeleteIfItExists(fileName);
        });
        app.MapPost(path("can-rename-file"), (Guid storeId, Guid ioId) => new { CanRename = server.GetIO(ioId).CanRenameFile });
        app.MapPost(path("rename-file"), (Guid storeId, Guid ioId, string fileName, string newFileName) => {
            ensurePrefix(storeId, ref fileName);
            ensurePrefix(storeId, ref newFileName);
            server.GetIO(ioId).RenameFile(fileName, newFileName);
        });
        app.MapPost(path("initiate-upload"), () => { return new { Value = Guid.NewGuid().ToString() }; });
        app.MapPost(path("upload-part"), async (HttpContext ctx, Guid uploadId) => {
            using var ioStream = server.TempIO.OpenAppend(uploadId.ToString());
            using var writeStream = new WriteStreamWrapper(ioStream);
            await ctx.Request.Body.CopyToAsync(writeStream);
        });
        app.MapPost(path("cancel-upload"), (HttpContext ctx, Guid uploadId) => server.TempIO.DeleteIfItExists(uploadId.ToString()));
        app.MapPost(path("complete-upload"), (HttpContext ctx, Guid storeId, Guid ioId, Guid uploadId, string fileName, bool overwrite) => {
            if (server.TempIO.DoesNotExistsOrIsEmpty(uploadId.ToString())) throw new Exception("Upload not found");
            var destIo = server.GetIO(ioId);
            ensurePrefix(storeId, ref fileName);
            var fileKeys = new FileKeyUtility(server.Containers[storeId].Settings?.LocalSettings?.FilePrefix);
            if (fileKeys.StateFileKey.ToLower() == fileName.ToLower()) {
                server.TempIO.DeleteIfItExists(uploadId.ToString());
                throw new Exception("Uploading state file is not allowed. ");
            }
            destIo.DeleteIfItExists(fileKeys.StateFileKey); // delete the state file to avoid old statefile and newer log file!
            if (destIo is IOProviderDisk diskIO && server.TempIO is IOProviderDisk tempDiskIO) {
                diskIO.MoveFile(tempDiskIO, uploadId.ToString(), fileName, overwrite);
                return;
            }
            using var ioSourceStream = server.TempIO.OpenRead(uploadId.ToString(), 0);
            if (!destIo.DoesNotExistsOrIsEmpty(fileName) && !overwrite) throw new Exception("File already exists");
            destIo.DeleteIfItExists(fileName);
            using var ioDestStream = destIo.OpenAppend(fileName);
            using var readStream = new ReadStreamWrapper(ioSourceStream);
            using var writeStream = new WriteStreamWrapper(ioDestStream);
            readStream.CopyTo(writeStream);
            ioSourceStream.Dispose();
            server.TempIO.DeleteIfItExists(uploadId.ToString());
        });
        app.MapPost(path("truncate-log"), (HttpContext ctx, Guid storeId, bool deleteOld) => {
            db(storeId).MaintenanceAsync(MaintenanceAction.TruncateLog);
            if (deleteOld) db(storeId).MaintenanceAsync(MaintenanceAction.DeleteOldLogs);
        });
        app.MapPost(path("save-index-states"), (HttpContext ctx, Guid storeId, bool forceRefresh, bool nodeSegmentsOnly) => db(storeId).Datastore.SaveIndexStates(forceRefresh, nodeSegmentsOnly));
        app.MapPost(path("reset-secondary-log-file"), (HttpContext ctx, Guid storeId) => db(storeId).MaintenanceAsync(MaintenanceAction.ResetSecondaryLogFile));
        app.MapPost(path("reset-state-and-indexes"), (HttpContext ctx, Guid storeId) => db(storeId).MaintenanceAsync(MaintenanceAction.ResetStateAndIndexes));
        app.MapPost(path("delete-state-and-indexes"), (HttpContext ctx, Guid storeId) => container(storeId).DeleteAllStateAndIndexFiles());
        app.MapPost(path("clear-cache"), (HttpContext ctx, Guid storeId) => db(storeId).Datastore.MaintenanceAsync(MaintenanceAction.ClearCache | MaintenanceAction.GarbageCollect));
        app.MapPost(path("info"), async (HttpContext ctx, Guid storeId) => {
            var store = container(storeId).Store;
            if (store == null) return null;
            return await store.Datastore.GetInfoAsync();
        });
        app.MapPost(path("clean-temp-files"), () => server.TempIO.GetFiles().ForEach(file => { try { server.TempIO.DeleteIfItExists(file.Key); } catch { } }));
        app.MapPost(path("get-size-temp-files"), () => new { TotalSize = server.TempIO.GetFiles().Sum(file => file.Size) });
        app.MapGet(path("download-truncated-db"), (Guid storeId, string namePrefix) => {
            namePrefix = string.Concat(namePrefix.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ' || c == '.'));
            if (namePrefix.Length > 100) namePrefix = namePrefix.Substring(0, 100);
            if (namePrefix.Length > 0 && !namePrefix.EndsWith(" ")) namePrefix += " ";
            var fileKey = Guid.NewGuid().ToString();
            db(storeId).Datastore.RewriteStore(false, fileKey, server.TempIO);
            var ioStream = server.TempIO.OpenRead(fileKey, 0);
            var stream = new ReadStreamWrapper(ioStream);
            var name = container(storeId).Settings.Name;
            if (string.IsNullOrEmpty(name)) name = "Database";
            var fileName = name + " " + DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ss") + ".bin";
            //var fileName = datastore.FileKeys.Log_NextFileKey(datastore.IO);
            return Results.File(stream, MediaTypeHeaderValue.Parse("application/octet-stream").ToString(), namePrefix + fileName);
        });
        app.MapGet(path("download-full-db"), (Guid storeId, string namePrefix) => {
            namePrefix = string.Concat(namePrefix.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ' || c == '.'));
            if (namePrefix.Length > 100) namePrefix = namePrefix.Substring(0, 100);
            if(namePrefix.Length > 0 && !namePrefix.EndsWith(" ")) namePrefix += " ";
            var fileKey = Guid.NewGuid().ToString();
            var datastore = container(storeId).Store!.Datastore;
            datastore.CopyStore(fileKey, server.TempIO);
            var ioStream = server.TempIO.OpenRead(fileKey, 0);
            var stream = new ReadStreamWrapper(ioStream);
            var name = container(storeId).Settings.Name;
            if (string.IsNullOrEmpty(name)) name = "Database";
            var fileName = name + " " + DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ss") + ".bin";
            //var fileName = datastore.FileKeys.Log_NextFileKey(datastore.IO);
            return Results.File(stream, MediaTypeHeaderValue.Parse("application/octet-stream").ToString(), namePrefix + fileName);
        });
        app.MapPost(path("copy-file"), (Guid storeId, Guid fromIoId, string fromFileName, Guid toIoId, string toIoFileName) => {
            ensurePrefix(storeId, ref fromFileName);
            ensurePrefix(storeId, ref toIoFileName);
            var io = server.GetIO(toIoId);
            io.CopyFile(fromFileName, toIoFileName);
        });
        app.MapPost(path("delete-all-but-db"), (Guid storeId) => {
            var settings = container(storeId).Settings;
            if (settings.LocalSettings == null) throw new Exception("LocalSettings is required for NodeStoreContainerSettings");
            var fileKeys = new FileKeyUtility(settings.LocalSettings.FilePrefix);
            IIOProvider io;
            if (!settings.IoDatabase.HasValue || settings.IoDatabase == Guid.Empty) throw new Exception("IoDatabase is required for NodeStoreContainerSettings");
            io = server.GetIO(settings.IoDatabase.Value);
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
            var io = server.GetIO(ioId);
            foreach (var file in fileKeys.GetAllFiles(io)) {
                if (file.Writers > 0 || file.Readers > 0) continue;
                io.DeleteIfItExists(file.Key);
            }
        });
    }
    void mapServer(WebApplication app, Func<string, string> path) {
        app.MapPost(path("get-store-containers"), () => {
            return server.Containers.Values.Select(c => new {
                c.Settings.Id,
                c.Settings.Name,
                c.Settings.Description,
                Status = c.GetStatusAndActivity(),
                c.Settings.IoDatabase,
            });
        });
        app.MapPost(path("get-default-store-id"), () => server.Settings.DefaultStoreId.ToString());
        app.MapPost(path("set-default-store-id"), (Guid storeId) => {
            server.Settings.DefaultStoreId = storeId;
            server.UpdateWAFServerSettingsFile();
        });
        //app.MapPost(path("set-master-credentials"), ([FromBody] dynamic settings) => {
        //    server.Settings.MasterUserName = settings.MasterUserName;
        //    server.Settings.MasterPassword = settings.MasterPassword;
        //    server.UpdateWAFServerSettingsFile();
        //});
        //app.MapPost(path("set-name-and-description"), ([FromBody] dynamic settings) => {
        //    server.Settings.Name = settings.Name;
        //    server.Settings.Description = settings.Description;
        //    server.UpdateWAFServerSettingsFile();
        //});
        app.MapPost(path("create-store"), () => {
            var id = Guid.NewGuid();
            var containerSettings = new NodeStoreContainerSettings() { Id = id, Name = "New Store" };
            var container = new NodeStoreContainer(containerSettings, server);
            server.Containers.Add(id, container);
            server.UpdateWAFServerSettingsFile();
            return containerSettings;
        });
        app.MapPost(path("remove-store"), (Guid storeId) => {
            container(storeId).Dispose();
            server.Containers.Remove(storeId);
            server.UpdateWAFServerSettingsFile();
        });
        app.MapPost(path("get-server-log"), () => server.GetStartUpLog().Select(e => { return new { Timestamp = e.Item1, Description = e.Item2 }; }).ToArray());
        app.MapPost(path("clear-server-log"), server.ClearStartUpLog);
    }
    void mapData(WebApplication app, Func<string, string> path) {
        app.MapPost(path("queue-re-index-all"), (Guid storeId) => {
            var allIds = db(storeId).Query<object>().SelectId().Execute();
            var transaction = db(storeId).CreateTransaction();
            foreach (var id in allIds) transaction.ReIndex(id);
            ThreadPool.QueueUserWorkItem(_ => { transaction.Execute(); });
            return allIds.Count;
        });
        app.MapPost(path("query"), (Guid storeId, QueryModel query) => server.GetStore(storeId).EvaluateForJsonAsync(query.Query, [.. query.Parameters.Select(ParameterModel.Convert)]));
        app.MapPost(path("execute"), (Guid storeId, ActionModel[] actions, bool flushToDisk) => server.GetStore(storeId).ExecuteAsync(actions, flushToDisk));
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
        app.MapPost(path("get-code"), (Guid storeId, bool addAttributes) => ModelGen.GenerateCSharpModelCode(db(storeId).Datastore.Datamodel, addAttributes));
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
        app.MapPost(path("get-system-trace"), (Guid storeId, int skip, int take) => db(storeId).Datastore.GetSystemTrace(skip, take));
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
        app.MapPost(path("populate"), (Guid storeId, int count, bool wikipediaData) => {
            var store = db(storeId);
            var sw = new Stopwatch();
            var chunkSize = 1000;
            var created = 0;
            var path = "C:\\WAF_Sources\\wikipedia\\wiki-articles.json"; // temporary hardcoded path to wikipedia data file...
            var seed = 0; // same every time for reproducible results
            using IArticleGenerator generator = wikipediaData ? new WikipediaArticleGenerator(path) : new RandomArticleGenerator(seed);
            // continue from existing count:
            var existingCount = store.Query<Demo.Models.DemoArticle>().Count();
            generator.Move(existingCount);
            while (true) {
                var create = Math.Min(chunkSize, count - created);
                if (create <= 0) break;
                var articles = generator.Many(create);
                sw.Start();
                store.Insert(articles);
                sw.Stop();
                created += create;
            }
            return new {
                CountCreated = count,
                ElapsedMs = sw.Elapsed.TotalMilliseconds
            };
        });
    }
}
