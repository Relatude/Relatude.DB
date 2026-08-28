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
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime;
using System.Text.Json;
namespace Relatude.DB.NodeServer.API;

public partial class ServerAPIMapper(RelatudeDBServer server) {
    string ApiUrlPublic => server.ApiUrlPublic;
    string ApiUrlRoot => server.ApiUrlRoot;
    static string[] splitFolderPath(string? folderPath) => folderPath?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
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
    public static string GlobalPublicStatusUrl = "relatude.db-public-status";

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
        app.MapPost(GlobalPublicStatusUrl, () => {
            return StatusResponse(server);
        });
        app.MapGet(path("favicon.ico"), (HttpContext ctx) => {
            var data = getBinaryResource("ClientUI.Images.favicon.ico");
            return Results.File(data, "image/x-icon", "favicon.ico");
        });
    }
    static DateTime _startUp = DateTime.UtcNow;
    public static SimpleStatus StatusResponse(RelatudeDBServer server) {
        var timeSinceStart = DateTime.UtcNow - _startUp;
        int nextUpdate;
        if (timeSinceStart.TotalSeconds < 5) nextUpdate = 200;
        else if (timeSinceStart.TotalSeconds < 30) nextUpdate = 1000;
        else if (timeSinceStart.TotalMinutes < 2) nextUpdate = 5000;
        else nextUpdate = 15000;
        var dbStatus = server.GetOpeningStatus();
        return new SimpleStatus {
            Starting = server.AnyRemaingToAutoOpenIncludingFailed,
            ProgressPercentage = dbStatus.ProgressPercentage,
            TimeRemainingMs = dbStatus.TimeRemainingMs,
            TimeElapsedMs = dbStatus.TimeElapsedMs,
            NextUpdate = nextUpdate,
            ResponseCheck = "Valid",
        };
    }
    public class SimpleStatus {
        public bool Starting { get; set; } = false;
        public int ProgressPercentage { get; set; } = 0;
        public int TimeRemainingMs { get; set; } = 0;
        public int TimeElapsedMs { get; set; } = 0;
        public string ResponseCheck { get; set; } = "";
        public int NextUpdate { get; set; } = 100;
    }
    class Credentials {
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";
        public bool Remember { get; set; } = false;
    }
    public static bool IsLocalConnection(ConnectionInfo connection) {
        if (connection.RemoteIpAddress != null) {
            if (connection.LocalIpAddress != null) {
                return connection.RemoteIpAddress.Equals(connection.LocalIpAddress);
            }
            return IPAddress.IsLoopback(connection.RemoteIpAddress);
        }
        return true;
    }
    void mapAuth(WebApplication app, Func<string, string> path) {
        app.MapGet(path("ping"), () => "pong");
        app.MapPost(path("ping"), () => "pong");
        app.MapPost(path("login"), async (HttpContext context, Credentials c) => {
            var requestIP = context.Connection.RemoteIpAddress + "";
            var isLocal = IsLocalConnection(context.Connection);
            var valid = await server.Authentication.AreCredentialsValid(c.UserName, c.Password, requestIP, isLocal);
            if (valid) {
                server.Authentication.LogIn(context, c.Remember);
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
        app.MapPost(path("uptime-in-ms"), () => server.UpTime.TotalMilliseconds);
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
            if (!server.GetContainers().Any(c => c.IsOpenOrOpening())) server.ResetIOProviders();
        });
        app.MapPost(path("cancel-opening"), (Guid storeId) => {
            try {
                container(storeId).CloseIfOpen();
                if (!server.GetContainers().Any(c => c.IsOpenOrOpening())) server.ResetIOProviders();
            } catch { }
        });
        app.MapPost(path("close-all-open-streams"), (Guid storeId, Guid ioId) => server.GetIO(ioId).CloseAllOpenStreams());
        app.MapPost(path("get-store-files"), (Guid storeId, Guid ioId) => server.GetIO(ioId).GetFiles());
        app.MapPost(path("can-have-folders"), (Guid storeId, Guid ioId) => new { CanHave = true });
        app.MapPost(path("get-folders"), (Guid storeId, Guid ioId) => server.GetIO(ioId).GetFoldersAsync([], true, true)); // kept for older clients
        // one level of the given folder ("" = the storage root): its files and subfolder stubs, no sizes computed
        app.MapPost(path("get-folder"), (Guid storeId, Guid ioId, string? folderPath) => server.GetIO(ioId).GetFolderAsync(splitFolderPath(folderPath), false, true));
        // the full tree of the given folder, used by the client side folder download to plan the files to fetch
        app.MapPost(path("get-folder-recursive"), (Guid storeId, Guid ioId, string? folderPath) => server.GetIO(ioId).GetFolderAsync(splitFolderPath(folderPath), true, true));
        // recursive size and counts of the given folder, on demand: walking a big tree can take a
        // while, so the client asks per folder instead of the listing computing it eagerly
        app.MapPost(path("get-folder-size"), async (Guid storeId, Guid ioId, string? folderPath) => {
            var folder = await server.GetIO(ioId).GetFolderAsync(splitFolderPath(folderPath), true, true);
            long size = 0; long fileCount = 0; long folderCount = 0;
            void sum(FolderMeta f) {
                foreach (var file in f.Files) { size += file.Size; fileCount++; }
                foreach (var sub in f.SubFolders) { folderCount++; sum(sub); }
            }
            sum(folder);
            return new { Size = size, FileCount = fileCount, FolderCount = folderCount };
        });
        app.MapPost(path("delete-folder"), (Guid storeId, Guid ioId, string folderName) => server.GetIO(ioId).DeleteFolderIfItExists(splitFolderPath(folderName)));
        app.MapPost(path("file-exist"), (Guid storeId, Guid ioId, string fileName) => !server.GetIO(ioId).DoesNotExistOrIsEmpty(fileName.SplitKey()));
        app.MapPost(path("backup-now"), (Guid storeId, Guid ioId, bool truncate, bool keepForever) => {
            if (ioId == Guid.Empty) {
                var settings = container(storeId).Settings;
                if (settings.IoBackup.HasValue && settings.IoBackup != Guid.Empty) ioId = settings.IoBackup.Value;
                else ioId = settings.IoDatabase ?? throw new Exception("No IoBackup or IoDatabase defined in settings. ");
            }
            db(storeId).Datastore.BackUpNow(truncate, keepForever, server.GetIO(ioId));
        });
        app.MapPost(path("cancel-rewrite-if-any"), (Guid storeId) => new { FileKey = db(storeId).Datastore.CancelRunningRewriteIfAny() });

        app.MapPost(path("is-file-key-legal"), (string fileKey) => new { IsLegal = FileKeyUtility.IsFileKeyValid(fileKey) });
        app.MapPost(path("is-file-prefix-legal"), (string filePrefix) => new { IsLegal = FileKeyUtility.IsFilePrefixValid(filePrefix, out _) });
        app.MapPost(path("get-file-key-of-db"), (Guid storeId, Guid ioId) => {
            var settings = container(storeId).Settings;
            if (settings.IoDatabase != ioId) return string.Empty;
            return FileKeyUtility.WAL_GetLatestFileKey(server.GetIO(ioId)).AsKeyString();
        });
        app.MapPost(path("get-file-key-of-db-next"), (Guid storeId, Guid ioId) => {
            var settings = container(storeId).Settings;
            if (settings.IoDatabase != ioId) return string.Empty;
            return FileKeyUtility.WAL_NextFileKey(server.GetIO(ioId)).AsKeyString();
        });
        app.MapGet(path("download-file"), (HttpContext ctx, Guid storeId, Guid ioId, string fileName) => {
            var fileKey = fileName.SplitKey();
            var io = server.GetIO(ioId);
            var contentType = MediaTypeHeaderValue.Parse("application/octet-stream").ToString();
            // Disk backed files are opened directly: engine owned files (e.g. the index stores) can be
            // OS locked without the provider knowing, and the provider's OpenRead retries such files
            // for minutes while holding the provider lock, stalling every other file operation. A
            // locked file fails fast as 423 instead, so a folder download can warn and keep going.
            if (io.TryGetLocalFilePath(fileKey, out var localFilePath)) {
                try {
                    var fileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return Results.File(fileStream, contentType, fileKey.FileName(), null, null, true);
                } catch (FileNotFoundException) {
                    return Results.NotFound();
                } catch (DirectoryNotFoundException) {
                    return Results.NotFound();
                } catch (IOException) {
                    return Results.StatusCode(StatusCodes.Status423Locked);
                }
            }
            var ioStream = io.OpenRead(fileKey, 0);
            var stream = ReadStreamWrapper.Wrap(ioStream);
            return Results.File(stream, contentType, fileKey.FileName(), null, null, true);
        });
        app.MapPost(path("delete-file"), (HttpContext ctx, Guid storeId, Guid ioId, string fileName) => {
            server.GetIO(ioId).DeleteFileIfItExists(fileName.SplitKey());
        });
        app.MapPost(path("can-rename-file"), (Guid storeId, Guid ioId) => new { CanRename = server.GetIO(ioId).CanRenameFile });
        app.MapPost(path("rename-file"), (Guid storeId, Guid ioId, string fileName, string newFileName) => {
            server.GetIO(ioId).RenameFile(fileName.SplitKey(), newFileName.SplitKey());
        });
        app.MapPost(path("initiate-upload"), () => { return new { Value = Guid.NewGuid().ToString() }; });
        app.MapPost(path("upload-part"), async (HttpContext ctx, Guid uploadId) => {
            using var ioStream = server.TempIO.OpenAppend([uploadId.ToString()]);
            using var writeStream = new WriteStreamWrapper(ioStream);
            await ctx.Request.Body.CopyToAsync(writeStream);
        });
        app.MapPost(path("cancel-upload"), (HttpContext ctx, Guid uploadId) => server.TempIO.DeleteFileIfItExists([uploadId.ToString()]));
        app.MapPost(path("complete-upload"), (HttpContext ctx, Guid storeId, Guid ioId, Guid uploadId, string fileName, bool overwrite) => {
            string[] uploadKey = [uploadId.ToString()];
            if (server.TempIO.DoesNotExistsOrIsEmpty(uploadKey)) throw new Exception("Upload not found");
            var destIo = server.GetIO(ioId);
            var fileKey = fileName.SplitKey();
            if (FileKeyUtility.StateFileKey.IsSameKey(fileKey)) {
                server.TempIO.DeleteFileIfItExists(uploadKey);
                throw new Exception("Uploading state file is not allowed. ");
            }
            destIo.DeleteFileIfItExists(FileKeyUtility.StateFileKey); // delete the state file to avoid old statefile and newer log file!
            if (destIo is IOProviderDisk diskIO && server.TempIO is IOProviderDisk tempDiskIO) {
                diskIO.MoveFile(tempDiskIO, uploadKey, fileKey, overwrite);
                return;
            }
            using var ioSourceStream = server.TempIO.OpenRead(uploadKey, 0);
            if (!destIo.DoesNotExistsOrIsEmpty(fileKey) && !overwrite) throw new Exception("File already exists");
            destIo.DeleteFileIfItExists(fileKey);
            using var ioDestStream = destIo.OpenAppend(fileKey);
            using var readStream = ReadStreamWrapper.Wrap(ioSourceStream);
            using var writeStream = new WriteStreamWrapper(ioDestStream);
            readStream.CopyTo(writeStream);
            ioSourceStream.Dispose();
            server.TempIO.DeleteFileIfItExists(uploadKey);
        });
        app.MapPost(path("truncate-log"), (HttpContext ctx, Guid storeId, bool deleteOld) => {
            var options = MaintenanceAction.TruncateLog;
            if (deleteOld) options |= MaintenanceAction.DeleteOldLogs;
            db(storeId).MaintenanceAsync(options);
        });
        app.MapPost(path("save-index-states"), (HttpContext ctx, Guid storeId, bool forceRefresh, bool nodeSegmentsOnly) => db(storeId).Datastore.SaveIndexStates(forceRefresh, nodeSegmentsOnly));
        app.MapPost(path("update-persisted-caches"), (HttpContext ctx, Guid storeId) => db(storeId).Datastore.MaintenanceAsync(MaintenanceAction.UpdatePersistedCaches));
        app.MapPost(path("reset-secondary-log-file"), (HttpContext ctx, Guid storeId) => db(storeId).MaintenanceAsync(MaintenanceAction.ResetSecondaryLogFile));
        app.MapPost(path("reset-state-and-indexes"), (HttpContext ctx, Guid storeId) => db(storeId).MaintenanceAsync(MaintenanceAction.ResetStateAndIndexes));
        app.MapPost(path("delete-state-and-indexes"), (HttpContext ctx, Guid storeId) => container(storeId).DeleteAllStateAndIndexFiles());
        app.MapPost(path("clear-cache"), (HttpContext ctx, Guid storeId) => db(storeId).Datastore.MaintenanceAsync(MaintenanceAction.ClearCache | MaintenanceAction.GarbageCollect));
        app.MapPost(path("info"), async (HttpContext ctx, Guid storeId) => {
            var store = container(storeId).Store;
            if (store == null) return null;
            return await store.Datastore.GetInfoAsync();
        });
        app.MapPost(path("clean-temp-files"), () => server.TempIO.GetFiles().ForEach(file => { try { server.TempIO.DeleteFileIfItExists(file.KeyOf()); } catch { } }));
        app.MapPost(path("get-size-temp-files"), () => new { TotalSize = server.TempIO.GetFiles().Sum(file => file.Size) });
        app.MapGet(path("download-truncated-db"), (Guid storeId, string namePrefix) => {
            namePrefix = string.Concat(namePrefix.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ' || c == '.'));
            if (namePrefix.Length > 100) namePrefix = namePrefix.Substring(0, 100);
            if (namePrefix.Length > 0 && !namePrefix.EndsWith(" ")) namePrefix += " ";
            string[] fileKey = [Guid.NewGuid().ToString()];
            db(storeId).Datastore.RewriteStore(false, fileKey, server.TempIO);
            var ioStream = server.TempIO.OpenRead(fileKey, 0);
            var stream = ReadStreamWrapper.Wrap(ioStream);
            var name = container(storeId).Settings.Name;
            if (string.IsNullOrEmpty(name)) name = "Database";
            var fileName = name + " " + DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ss") + ".bin";
            //var fileName = FileKeyUtility.Log_NextFileKey(datastore.IO);
            return Results.File(stream, MediaTypeHeaderValue.Parse("application/octet-stream").ToString(), namePrefix + fileName);
        });
        app.MapGet(path("download-full-db"), (Guid storeId, string namePrefix) => {
            namePrefix = string.Concat(namePrefix.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ' || c == '.'));
            if (namePrefix.Length > 100) namePrefix = namePrefix.Substring(0, 100);
            if (namePrefix.Length > 0 && !namePrefix.EndsWith(" ")) namePrefix += " ";
            string[] fileKey = [Guid.NewGuid().ToString()];
            var datastore = container(storeId).Store!.Datastore;
            datastore.CopyStore(fileKey, server.TempIO);
            var ioStream = server.TempIO.OpenRead(fileKey, 0);
            var stream = ReadStreamWrapper.Wrap(ioStream);
            var name = container(storeId).Settings.Name;
            if (string.IsNullOrEmpty(name)) name = "Database";
            var fileName = name + " " + DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ss") + ".bin";
            //var fileName = FileKeyUtility.Log_NextFileKey(datastore.IO);
            return Results.File(stream, MediaTypeHeaderValue.Parse("application/octet-stream").ToString(), namePrefix + fileName);
        });
        app.MapPost(path("copy-file"), (Guid storeId, Guid fromIoId, string fromFileName, Guid toIoId, string toIoFileName) => {
            var io = server.GetIO(toIoId);
            io.CopyFile(fromFileName.SplitKey(), toIoFileName.SplitKey());
        });

        // scanning the file stores can take a while on big databases, so these run as background
        // jobs the client polls for progress and can cancel
        app.MapPost(path("delete-unreferenced-files-start"), (Guid storeId, bool countOnly) => {
            if (db(storeId).Datastore is not DataStoreLocal local) throw new Exception("Only supported for local data stores. ");
            var job = startFileScanJob(storeId, "unreferenced files", async j =>
                (object)await local.DeleteUnreferencedFilesAsync(countOnly, j.SetProgress, j.Cancellation.Token));
            return new { JobId = job.Id };
        });
        app.MapPost(path("delete-unreferenced-files-progress"), (Guid storeId, Guid jobId) => {
            var job = getFileScanJob(jobId);
            var result = job.Result as DataStores.Files.DeleteUnReferenceResult;
            return new {
                job.State,
                job.Description,
                job.Percent,
                job.Error,
                TotalBytesDeleted = result?.TotalBytesDeleted ?? 0,
                TotalFilesDeleted = result?.TotalFilesDeleted ?? 0,
                TotalFoldersDeleted = result?.TotalFoldersDeleted ?? 0,
            };
        });
        app.MapPost(path("delete-unreferenced-files-cancel"), (Guid storeId, Guid jobId) => getFileScanJob(jobId).Cancellation.Cancel());

        // the other direction: every file value in the database checked against the store it points at
        app.MapPost(path("find-missing-files-start"), (Guid storeId) => {
            if (db(storeId).Datastore is not DataStoreLocal local) throw new Exception("Only supported for local data stores. ");
            var job = startFileScanJob(storeId, "missing files", async j =>
                (object)await local.FindMissingFilesAsync(j.SetProgress, j.Cancellation.Token));
            return new { JobId = job.Id };
        });
        app.MapPost(path("find-missing-files-progress"), (Guid storeId, Guid jobId) => {
            var job = getFileScanJob(jobId);
            var result = job.Result as DataStores.Files.MissingFilesResult;
            return new {
                job.State,
                job.Description,
                job.Percent,
                job.Error,
                NodesScanned = result?.NodesScanned ?? 0,
                FilesChecked = result?.FilesChecked ?? 0,
                MissingCount = result?.MissingCount ?? 0,
                MissingBytes = result?.MissingBytes ?? 0,
                ListTruncated = result?.ListTruncated ?? false,
                // the result, and with it the list, is only set once the job is done, so the entries
                // are sent once instead of being repeated on every poll
                Missing = result?.Missing ?? Array.Empty<DataStores.Files.MissingFileInfo>(),
            };
        });
        app.MapPost(path("find-missing-files-cancel"), (Guid storeId, Guid jobId) => getFileScanJob(jobId).Cancellation.Cancel());
    }
    class FileScanJob {
        public const string Running = "running";
        public const string Done = "done";
        public const string Cancelled = "cancelled";
        public const string Failed = "failed";
        public Guid Id { get; } = Guid.NewGuid();
        public Guid StoreId;
        public string Kind = string.Empty;
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public CancellationTokenSource Cancellation { get; } = new();
        // written by the job task, read by polling requests; torn values are harmless for display
        public volatile string State = Running;
        public volatile string Description = "";
        public volatile int Percent;
        public volatile string? Error;
        // assigned before State turns to Done, so a poll that sees Done also sees the result
        public object? Result;
        public void SetProgress(string description, int percent) {
            Description = description;
            Percent = percent;
        }
    }
    static readonly Dictionary<Guid, FileScanJob> _fileScanJobs = [];
    static FileScanJob getFileScanJob(Guid jobId) {
        lock (_fileScanJobs) {
            if (_fileScanJobs.TryGetValue(jobId, out var job)) return job;
            throw new Exception("File scan job not found. ");
        }
    }
    /// <summary>Runs one file store scan in the background, at most one of each kind per store.
    /// Finished jobs are kept for an hour, so a client can still pick up the result.</summary>
    static FileScanJob startFileScanJob(Guid storeId, string kind, Func<FileScanJob, Task<object>> run) {
        var job = new FileScanJob { StoreId = storeId, Kind = kind };
        lock (_fileScanJobs) {
            if (_fileScanJobs.Values.Any(j => j.StoreId == storeId && j.Kind == kind && j.State == FileScanJob.Running))
                throw new Exception("A " + kind + " job is already running for this store. ");
            foreach (var old in _fileScanJobs.Values.Where(j => j.State != FileScanJob.Running && DateTime.UtcNow - j.StartedUtc > TimeSpan.FromHours(1)).ToArray())
                _fileScanJobs.Remove(old.Id);
            _fileScanJobs[job.Id] = job;
        }
        _ = Task.Run(async () => {
            try {
                job.Result = await run(job);
                job.State = FileScanJob.Done;
            } catch (OperationCanceledException) {
                job.State = FileScanJob.Cancelled;
            } catch (Exception e) {
                job.Error = e.Message;
                job.State = FileScanJob.Failed;
            }
        });
        return job;
    }
    void mapServer(WebApplication app, Func<string, string> path) {
        app.MapPost(path("get-store-containers"), () => {
            return server.GetContainers().Select(c => new {
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
            lock (server.Containers) server.Containers.Add(id, container);
            server.UpdateWAFServerSettingsFile();
            return containerSettings;
        });
        app.MapPost(path("remove-store"), (Guid storeId) => {
            container(storeId).Dispose();
            lock (server.Containers) server.Containers.Remove(storeId);
            server.UpdateWAFServerSettingsFile();
        });
        app.MapPost(path("get-server-log"), () => server.GetStartUpLog().Select(e => { return new { Timestamp = e.Item1, Description = e.Item2 }; }).ToArray());
        app.MapPost(path("clear-server-log"), server.ClearStartUpLog);
        app.MapPost(path("get-restart-info"), () => server.GetRestartCapabilities());
        app.MapPost(path("soft-restart"), () => {
            if (!server.GetRestartCapabilities().CanSoftRestart) throw new Exception("Soft restart is not allowed on this server. ");
            if (server.IsRestarting) return new { Started = false, Message = "A restart is already running." };
            // closing and rebuilding every database takes far longer than a request should, so this only
            // starts it: the admin UI watches RestartCount to see it land
            _ = Task.Run(async () => {
                try { await server.SoftRestartAsync(); } catch { } // SoftRestartAsync has already logged it
            });
            return new { Started = true, Message = "Soft restart started." };
        });
        app.MapPost(path("stop-host"), () => {
            if (!server.GetRestartCapabilities().CanStopHost) throw new Exception("Stopping the host is not allowed on this server. ");
            var started = server.StopHost();
            return new { Started = started, Message = started ? "The host is stopping." : "This host cannot be stopped from here." };
        });
    }
    void mapData(WebApplication app, Func<string, string> path) {
        app.MapPost(path("queue-re-index-all"), (Guid storeId) => {
            var allIds = db(storeId).Query<object>().SelectId().Execute();
            var transaction = db(storeId).CreateTransaction();
            foreach (var id in allIds) transaction.ReIndex(id);
            ThreadPool.QueueUserWorkItem(_ => { transaction.Execute(); });
            return allIds.Count;
        });
        // a context in the request body reads as that user, culture and visibility; without one the
        // query reads with the context of the store, as everywhere else
        app.MapPost(path("query"), (Guid storeId, QueryModel query) => server.GetStore(storeId)
            .EvaluateForJsonAsync(query.Query, [.. query.Parameters.Select(ParameterModel.Convert)], QueryContextModel.Convert(query.Context)));
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
            // if linux:
            if (Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX) {
                // convert to WSL mapped path:
                path = "/mnt/c/WAF_Sources/wikipedia/wiki-articles.json";
            }
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
