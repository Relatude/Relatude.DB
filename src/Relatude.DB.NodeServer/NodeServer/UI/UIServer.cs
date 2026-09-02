using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.NodeServer.API;
using Relatude.DB.NodeServer.Json;
using System.Text.Json;
namespace Relatude.DB.NodeServer.UI;
/// <summary>
/// The backend of the admin UI. All communication runs over two routes:
///   GET  {ApiUrlRoot}/ui/stream   - one SSE connection carrying all server-to-client push (UIEventStream)
///   POST {ApiUrlRoot}/ui/command  - all client-to-server requests, dispatched on command type (UICommands)
/// Both live under ApiUrlRoot, so the standard admin authentication middleware covers them.
/// The UI itself (built from src/Relatude.DB.UI into the embedded ClientUI resources) is served
/// at {ApiUrlRoot}, with its files under the public {ApiUrlRoot}/auth/ so a browser can load them
/// before anyone has logged in.
/// </summary>
public sealed class UIServer {
    const int containerWatchIntervalMs = 1000;
    readonly RelatudeDBServer _server;
    readonly Timer _containerWatch;
    readonly UIQuery _query;
    string? _lastContainersJson;
    public UIEventStream Events { get; } = new();
    public UICommands Commands { get; }
    internal UIServer(RelatudeDBServer server) {
        _server = server;
        Commands = new UICommands(server);
        registerBuiltInCommands();
        new UISettings(server).Register(Commands);
        new UILogs(server).Register(Commands);
        new UIDashboard(server).Register(Commands);
        new UITasks(server).Register(Commands);
        new UIDemo(server).Register(Commands);
        _query = new UIQuery(server);
        _query.Register(Commands);
        _containerWatch = new Timer(_ => watchContainers(), null, containerWatchIntervalMs, Timeout.Infinite);
    }
    // broadcasts a "containers" event whenever the container list changes (state, node count, name),
    // so every connected UI stays live without polling. Does no work while nobody is connected.
    void watchContainers() {
        try {
            if (Events.ConnectionCount > 0) {
                var containers = buildContainers();
                var json = JsonSerializer.Serialize(containers, RelatudeDBJsonOptions.SSE);
                if (json != _lastContainersJson) {
                    _lastContainersJson = json;
                    Events.Broadcast("containers", containers);
                }
            }
        } catch (Exception error) {
            RelatudeDBServer.Trace("UI container watch error: " + error.Message);
        } finally {
            _containerWatch!.Change(containerWatchIntervalMs, Timeout.Infinite);
        }
    }
    object[] buildContainers() {
        return [.. _server.GetContainers().Select(c => {
            long? nodeCount = null;
            var conversionCount = 0;
            var taskCount = 0;
            if (c.IsOpen()) {
                // a container closing mid-request should not fail the snapshot
                try { nodeCount = c.Store!.Count(); } catch { }
                // what the file conversion queue still owes, so the nav badge is live on every page
                // without a poll of its own. Counting is walking a short in-memory list.
                try {
                    var conversions = c.Store!.Datastore.GetConversions();
                    conversionCount = conversions.Running + conversions.Queued;
                } catch { }
                // and what the background task queues still owe, for the same reason: the Tasks badge
                // has to be live on every page, not only on the one that polls the queues
                try { taskCount = queuedTasks(c.Store!.Datastore); } catch { }
            }
            return (object)new {
                c.Settings.Id,
                Name = string.IsNullOrEmpty(c.Settings.Name) ? c.Settings.Id.ToString() : c.Settings.Name,
                State = c.HasFailed ? "Error" : c.Store?.State.ToString() ?? "Closed",
                NodeCount = nodeCount,
                ConversionCount = conversionCount,
                TaskCount = taskCount,
            };
        })];
    }
    internal void Map(WebApplication app) {
        var path = _server.ApiUrlRoot + "/ui/";
        app.MapGet(path + "stream", Events.Connect);
        app.MapPost(path + "command", (Delegate)Commands.Execute); // Delegate overload, so the returned IResult is written to the response
        // file uploads stream the raw request body straight into the IO provider (binary, so not a command)
        app.MapPost(path + "upload", async (HttpContext ctx, Guid ioId, string key) => {
            var sizeFeature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature != null && !sizeFeature.IsReadOnly) sizeFeature.MaxRequestBodySize = null; // database files exceed the default limit
            var fileKey = key.SplitKey();
            if (fileKey.Length == 0 || fileKey.Any(segment => !FileKeyUtility.IsFileKeyValid(segment))) {
                return Results.BadRequest(new { error = "Invalid file key. " });
            }
            if (FileKeyUtility.State_IsStateFileKey(fileKey)) return Results.BadRequest(new { error = "Uploading the state file is not allowed. " });
            var io = _server.GetIO(ioId);
            io.DeleteFileIfItExists(fileKey);
            try {
                using var ioStream = io.OpenAppend(fileKey);
                using var writeStream = new WriteStreamWrapper(ioStream);
                await ctx.Request.Body.CopyToAsync(writeStream, ctx.RequestAborted);
            } catch (OperationCanceledException) {
                io.DeleteFileIfItExists(fileKey); // no half files from a cancelled upload
                throw;
            }
            return Results.Ok();
        });
        // the query page's csv export (a file download, so not a command): the same search payload
        // the page runs, streamed back as rows instead of counted into facets
        app.MapPost(path + "query-csv", async (HttpContext ctx, UIQuery.SearchPayload payload) => {
            try {
                await _query.WriteCsv(ctx, payload);
            } catch (Exception error) when (!ctx.Response.HasStarted) {
                // everything that can fail (the store, the query) happens before the first row is
                // written, so until then the client can still be told what went wrong
                return Results.Json(new { error = error.Message }, RelatudeDBJsonOptions.Default, statusCode: 500);
            }
            return Results.Empty;
        });
        // the previews and the full size view of a file property in the query form (binary, so not a
        // command, and a GET so an <img> or a <video> can point straight at it): "p" is the property
        // path the file sits at, "v" its version, which is only there to keep a replaced file from
        // being served out of the browser cache
        app.MapGet(path + "media", async (HttpContext ctx, Guid storeId, string? p, int? w, int? h, bool? original) => {
            try {
                return await _query.WriteMedia(ctx, storeId, p, w, h, original == true);
            } catch (Exception error) when (!ctx.Response.HasStarted) {
                return Results.Json(new { error = error.Message }, RelatudeDBJsonOptions.Default, statusCode: 500);
            }
        });
        // zip downloads (binary, so not commands): GET zips a whole folder (also the url behind
        // dragging a folder out to the desktop), POST zips a set of selected files
        app.MapGet(path + "zip", async (HttpContext ctx, Guid ioId, string? folder) => {
            return await zipToResponse(ctx, ioId, null, folder);
        });
        app.MapPost(path + "zip", async (HttpContext ctx, ZipRequestPayload p) => {
            return await zipToResponse(ctx, p.IoId, p.Keys, p.BasePath);
        });
        mapStaticUI(app);
    }
    // Streams a zip of the given files (or of a whole folder when keys is null). Every file is
    // test-opened first: a locked file stops the request with 423 before any zip bytes are
    // written, so the client never receives a broken archive.
    async Task<IResult> zipToResponse(HttpContext ctx, Guid ioId, string[]? keys, string? folderPath) {
        var io = _server.GetIO(ioId);
        List<string> fileKeys;
        string zipName, prefixToStrip;
        if (keys == null) {
            var folder = splitFolderPath(folderPath);
            var meta = await io.GetFolderAsync(folder, true, true);
            fileKeys = [];
            void collect(FolderMeta f) {
                foreach (var file in f.Files) fileKeys.Add(file.Key);
                foreach (var sub in f.SubFolders) collect(sub);
            }
            collect(meta);
            zipName = (folder.Length == 0 ? "storage-root" : folder[^1]) + ".zip";
            // entries keep the folder's own name, so extracting gives one folder, not loose files
            prefixToStrip = folder.Length <= 1 ? "" : string.Join('/', folder[..^1]) + "/";
        } else {
            fileKeys = [.. keys];
            zipName = "files.zip";
            prefixToStrip = string.IsNullOrEmpty(folderPath) ? "" : folderPath + "/";
        }
        if (fileKeys.Count == 0) return Results.BadRequest(new { error = "No files to zip. " });
        var locked = await lockedFilesAsync(io, fileKeys);
        if (locked.Count > 0) return Results.Json(new { error = "Some files are locked. ", locked }, RelatudeDBJsonOptions.Default, statusCode: 423);
        // ZipArchive writes synchronously when entries and the archive close
        var bodyControl = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
        if (bodyControl != null) bodyControl.AllowSynchronousIO = true;
        ctx.Response.ContentType = "application/zip";
        ctx.Response.Headers.ContentDisposition = "attachment; filename=\"" + zipName + "\"";
        using var zip = new System.IO.Compression.ZipArchive(ctx.Response.Body, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true);
        foreach (var key in fileKeys) {
            var entryName = prefixToStrip.Length > 0 && key.StartsWith(prefixToStrip) ? key[prefixToStrip.Length..] : key;
            var entry = zip.CreateEntry(entryName, System.IO.Compression.CompressionLevel.Fastest);
            using var entryStream = entry.Open();
            using var source = ReadStreamWrapper.Wrap(io.OpenRead(key.SplitKey(), 0));
            await source.CopyToAsync(entryStream, ctx.RequestAborted);
        }
        return Results.Empty;
    }
    // Metadata based, NOT test-opening: opening a held file goes through FileOpenRetry, which
    // waits up to 30s for the lock to clear - the wrong behavior for a quick pre-check. A file
    // with an open write stream cannot be copied consistently, so it counts as locked. Folder
    // listings carry the lock counts (GetFiles only covers the system folders), one per parent.
    static async Task<List<string>> lockedFilesAsync(IIOProvider io, IEnumerable<string> keys) {
        var blocked = new List<string>();
        foreach (var group in keys.GroupBy(key => string.Join('/', key.SplitKey()[..^1]), StringComparer.OrdinalIgnoreCase)) {
            var folder = await io.GetFolderAsync(group.Key.SplitKey(), false, true);
            var byKey = folder.Files.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var key in group) {
                if (!byKey.TryGetValue(key, out var meta)) blocked.Add(key + " (not found)");
                else if (meta.Writers > 0) blocked.Add(key + " (being written)");
            }
        }
        return blocked;
    }
    void mapStaticUI(WebApplication app) {
        // The page itself sits on the admin root, which the authentication middleware lets through
        // unauthenticated (it is the login screen). Its files have to be readable before the login as
        // well, so they go under {ApiUrlRoot}/auth/, the public segment. Everything the UI calls once
        // it is running ({ApiUrlRoot}/ui/...) requires authentication as usual.
        var root = _server.ApiUrlRoot;
        var files = _server.ApiUrlPublic; // ends with '/'
        string html, js, css;
        try {
            html = ServerAPIMapper.GetResource("ClientUI.index.html");
            js = ServerAPIMapper.GetResource("ClientUI.index.js");
            css = ServerAPIMapper.GetResource("ClientUI.index.css");
        } catch (Exception error) {
            // The UI is embedded when this assembly is compiled, so a build made before the vite output
            // existed - or one made while vite had emptied NodeServer/ClientUI - has nothing to serve.
            // The url is mapped anyway: a bare 404 here sends everyone looking for a routing problem
            // that is not there, so the response says what is actually missing instead.
            var message = "The admin UI is not part of this build of Relatude.DB.NodeServer (" + error.Message
                + "). Build it with \"npm install\" and \"npm run build\" in src/Relatude.DB.UI, then rebuild"
                + " Relatude.DB.NodeServer in the configuration you are running (Debug and Release embed it"
                + " separately). ";
            RelatudeDBServer.Trace(message);
            _server.Log(message);
            app.MapGet(root, (HttpContext ctx) => {
                ctx.Response.StatusCode = StatusCodes.Status501NotImplemented;
                ctx.Response.ContentType = "text/html";
                return "<html><body><h3>The admin UI is not built into this server. </h3><p>"
                    + System.Net.WebUtility.HtmlEncode(message) + "</p></body></html>";
            });
            return;
        }
        // a unique url per version: unchanged UI stays cached by the browser, new versions bypass the cache
        var hash = js.XXH64Hash() ^ css.XXH64Hash();
        html = html
            .Replace("./index.js", files + hash + ".js")
            .Replace("./index.css", files + hash + ".css")
            .Replace("./favicon.ico", files + "favicon.ico");
        app.MapGet(root, (HttpContext ctx) => {
            ctx.Response.ContentType = "text/html";
            return html;
        });
        app.MapGet(files + hash + ".js", (HttpContext ctx) => {
            ctx.Response.ContentType = "text/javascript";
            ctx.Response.Headers.Append("Cache-Control", "public, max-age=315360000");
            return js;
        });
        app.MapGet(files + hash + ".css", (HttpContext ctx) => {
            ctx.Response.ContentType = "text/css";
            ctx.Response.Headers.Append("Cache-Control", "public, max-age=315360000");
            return css;
        });
        var favicon = ServerAPIMapper.GetBinaryResourceOrNull("ClientUI.favicon.ico");
        if (favicon != null) {
            app.MapGet(files + "favicon.ico", (HttpContext ctx) => {
                ctx.Response.Headers.Append("Cache-Control", "public, max-age=86400");
                return Results.File(favicon, "image/x-icon", "favicon.ico");
            });
        }
    }
    static string[] splitFolderPath(string? folderPath) => folderPath?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
    /// <summary>Where the file conversion engine keeps its cache: the "converted" folder of the
    /// index IO provider, falling back to the database one when no separate index provider is
    /// configured (the same fallback DataStoreLocal makes for its converter IO provider).</summary>
    (IIOProvider Io, string[] Folder) convertedCache(NodeStoreContainer c) {
        var ioId = c.Settings.IoIndexes is Guid indexes && indexes != Guid.Empty ? indexes : c.Settings.IoDatabase;
        if (ioId is not Guid id || id == Guid.Empty) throw new Exception("No IO provider configured, so there is no converted file cache. ");
        return (_server.GetIO(id), [FileKeyUtility.ConvertedFolderName]);
    }
    static async Task<(long Files, long Bytes)> folderTotals(IIOProvider io, string[] folder) {
        long files = 0, bytes = 0;
        void sum(FolderMeta f) {
            foreach (var file in f.Files) { bytes += file.Size; files++; }
            foreach (var sub in f.SubFolders) sum(sub);
        }
        sum(await io.GetFolderAsync(folder, true, true));
        return (files, bytes);
    }
    // the property a conversion belongs to, by name rather than by id - the id says nothing to
    // whoever is reading the page, and a datamodel that no longer has it says nothing either
    static string? propertyName(Datamodels.Datamodel? datamodel, Common.PropertyPath? property) {
        if (property == null || datamodel == null) return null;
        return datamodel.Properties.TryGetValue(property.PropertyId, out var model) ? model.CodeName : null;
    }
    // Tasks waiting or running, across the memory queue and the persisted one - a text index
    // rebuild lands in the persisted queue, everything else may be in either.
    static int queuedTasks(DataStores.IDataStore datastore) {
        static int count(Tasks.TaskQueue? queue) {
            if (queue == null) return 0;
            return queue.CountTasks(Tasks.BatchState.Pending) + queue.CountTasks(Tasks.BatchState.Running);
        }
        return count(datastore.TaskQueue) + count(datastore.TaskQueuePersisted);
    }

    NodeStoreContainer getContainer(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Container not found. ");
        return c;
    }
    Guid getBackupIoId(NodeStoreContainer c) {
        var s = c.Settings;
        if (s.IoBackup.HasValue && s.IoBackup != Guid.Empty) return s.IoBackup.Value;
        return s.IoDatabase ?? throw new Exception("No backup or database IO provider configured. ");
    }
    void registerBuiltInCommands() {
        Commands.Register("ping", ctx => new { Pong = true, ServerTimeUtc = DateTime.UtcNow });
        Commands.Register("server-info", ctx => new {
            Version = typeof(UIServer).Assembly.GetName().Version?.ToString(),
            UpTimeMs = _server.UpTime.TotalMilliseconds,
            Containers = buildContainers(),
        });
        Commands.Register("server-overview", ctx => {
            var containers = _server.GetContainers();
            var restart = _server.GetRestartCapabilities();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return (object?)new {
                ServerName = _server.Settings.Name,
                Version = typeof(UIServer).Assembly.GetName().Version?.ToString(),
                UpTimeMs = _server.UpTime.TotalMilliseconds,
                Machine = Environment.MachineName,
                Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ProcessorCount = Environment.ProcessorCount,
                ProcessMemoryBytes = process.WorkingSet64,
                ManagedMemoryBytes = GC.GetTotalMemory(false),
                AdminPath = _server.ApiUrlRoot,
                SettingsFile = _server.Settings.DBSettingsFilePath ?? Defaults.SettingsFileName,
                DefaultDatabase = containers.FirstOrDefault(c => c.Settings.Id == _server.Settings.DefaultStoreId)?.Settings.Name,
                Restart = new { restart.CanSoftRestart, restart.CanStopHost },
                Containers = containers.Select(c => {
                    long? nodeCount = null;
                    if (c.IsOpen()) {
                        try { nodeCount = c.Store!.Count(); } catch { }
                    }
                    var io = c.Settings.IOSettings?.FirstOrDefault(io => io.Id == c.Settings.IoDatabase);
                    return new {
                        c.Settings.Id,
                        Name = string.IsNullOrEmpty(c.Settings.Name) ? c.Settings.Id.ToString() : c.Settings.Name,
                        State = c.HasFailed ? "Error" : c.Store?.State.ToString() ?? "Closed",
                        NodeCount = nodeCount,
                        Provider = io == null ? null : string.IsNullOrEmpty(io.Name) ? io.IOType.ToString() : io.Name,
                    };
                }),
                ServerLog = _server.GetStartUpLog().TakeLast(100).Select(e => new { TimeUtc = e.Item1, Message = e.Item2 }),
                StartupExceptions = containers.Where(c => c.StartUpException != null).Select(c => new {
                    Container = string.IsNullOrEmpty(c.Settings.Name) ? c.Settings.Id.ToString() : c.Settings.Name,
                    Message = c.StartUpException!.Message,
                    TimeUtc = c.StartUpExceptionDateTimeUTC,
                }),
            };
        });
        // the IO providers of one database container, for the files section
        Commands.Register("io-list", ctx => {
            var p = ctx.Payload<IoListPayload>();
            var c = getContainer(p.StoreId);
            return (object?)(c.Settings.IOSettings ?? []).Select(io => new {
                io.Id,
                Name = string.IsNullOrEmpty(io.Name) ? io.IOType.ToString() : io.Name,
                Type = io.IOType.ToString(),
            });
        });
        // the given folder ("" = the storage root): its files and subfolder stubs, or the whole
        // tree below it when recursive (used to plan folder downloads)
        Commands.Register("io-folder", async ctx => {
            var p = ctx.Payload<IoFolderPayload>();
            return (object?)await _server.GetIO(p.IoId).GetFolderAsync(splitFolderPath(p.Path), p.Recursive, true);
        });
        // recursive size and counts, on demand: walking a big tree can take a while
        Commands.Register("io-folder-size", async ctx => {
            var p = ctx.Payload<IoFolderPayload>();
            var folder = await _server.GetIO(p.IoId).GetFolderAsync(splitFolderPath(p.Path), true, true);
            long size = 0, fileCount = 0, folderCount = 0;
            void sum(FolderMeta f) {
                foreach (var file in f.Files) { size += file.Size; fileCount++; }
                foreach (var sub in f.SubFolders) { folderCount++; sum(sub); }
            }
            sum(folder);
            return (object?)new { Size = size, FileCount = fileCount, FolderCount = folderCount };
        });
        // removes the (by then empty) folder itself; the UI deletes the files first, for progress
        Commands.Register("io-delete-folder", ctx => {
            var p = ctx.Payload<IoFolderPayload>();
            var folderPath = splitFolderPath(p.Path);
            if (folderPath.Length == 0) throw new Exception("The storage root cannot be deleted. ");
            _server.GetIO(p.IoId).DeleteFolderIfItExists(folderPath);
            return (object?)new { Deleted = true };
        });
        // checks each file for open write streams, so the client can stop a zip download
        // before it starts instead of failing halfway through a stream
        Commands.Register("io-check-locks", async ctx => {
            var p = ctx.Payload<IoDeleteFilesPayload>();
            return (object?)new { Locked = await lockedFilesAsync(_server.GetIO(p.IoId), p.Keys) };
        });
        Commands.Register("io-delete-files", ctx => {
            var p = ctx.Payload<IoDeleteFilesPayload>();
            var io = _server.GetIO(p.IoId);
            var deleted = 0;
            var errors = new List<string>();
            foreach (var key in p.Keys) {
                try {
                    io.DeleteFileIfItExists(key.SplitKey());
                    deleted++;
                } catch (Exception error) {
                    errors.Add(key + ": " + error.Message);
                }
            }
            return (object?)new { Deleted = deleted, Errors = errors };
        });
        // ---- storage section: backups, database download / upload ----
        Commands.Register("backup-list", ctx => {
            var p = ctx.Payload<IoListPayload>();
            var c = getContainer(p.StoreId);
            var ioId = getBackupIoId(c);
            var io = _server.GetIO(ioId);
            return (object?)new {
                IoId = ioId,
                Files = FileKeyUtility.WAL_GetAllBackUpFileKeys(io).Select(key => new {
                    Key = key.AsKeyString(),
                    Name = key.FileName(),
                    Size = io.GetFileSizeOrZeroIfUnknown(key),
                    TimeUtc = FileKeyUtility.WAL_GetBackUpDateTimeFromFileKey(key),
                    KeepForever = FileKeyUtility.WAL_KeepForever(key),
                }).OrderByDescending(f => f.TimeUtc),
            };
        });
        Commands.Register("backup-now", ctx => {
            var p = ctx.Payload<BackupNowPayload>();
            var c = getContainer(p.StoreId);
            var store = c.Store ?? throw new Exception("The database must be open to create a backup. ");
            if (store.State != DataStoreState.Open) throw new Exception("The database must be open to create a backup. ");
            store.Datastore.BackUpNow(p.Truncate, p.KeepForever, _server.GetIO(getBackupIoId(c)));
            return (object?)new { Done = true };
        });
        // copies a backup into place as the next WAL file key (the old current file is kept)
        // and clears the state file; the database must be closed, the UI reopens it after
        Commands.Register("backup-restore", ctx => {
            var p = ctx.Payload<BackupRestorePayload>();
            var c = getContainer(p.StoreId);
            if (c.IsOpenOrOpening()) throw new Exception("The database must be closed first. ");
            var backupIo = _server.GetIO(getBackupIoId(c));
            var dbIo = _server.GetIO(c.Settings.IoDatabase ?? throw new Exception("No database IO provider configured. "));
            var sourceKey = p.Key.SplitKey();
            if (backupIo.DoesNotExistOrIsEmpty(sourceKey)) throw new Exception("Backup not found. ");
            var destKey = FileKeyUtility.WAL_NextFileKey(dbIo);
            using (var source = backupIo.OpenRead(sourceKey, 0))
            using (var dest = dbIo.OpenAppend(destKey)) {
                using var readStream = ReadStreamWrapper.Wrap(source);
                using var writeStream = new WriteStreamWrapper(dest);
                readStream.CopyTo(writeStream);
            }
            FileKeyUtility.State_DeleteAll(dbIo); // an old state must never pair with the restored log
            return (object?)new { NewKey = destKey.AsKeyString() };
        });
        // log size, snapshot staleness and truncation potential, plus old WAL files no longer in use
        Commands.Register("db-maintenance-info", async ctx => {
            var p = ctx.Payload<IoListPayload>();
            var c = getContainer(p.StoreId);
            long unusedFiles = 0, unusedBytes = 0;
            if (c.Settings.IoDatabase is Guid dbIoId) {
                var io = _server.GetIO(dbIoId);
                var latest = FileKeyUtility.WAL_GetLatestFileKey(io);
                foreach (var key in FileKeyUtility.WAL_GetAllFileKeys(io)) {
                    if (key.IsSameKey(latest)) continue;
                    unusedFiles++;
                    unusedBytes += io.GetFileSizeOrZeroIfUnknown(key);
                }
            }
            var store = c.Store;
            if (store == null || store.State != DataStoreState.Open) {
                return (object?)new { Open = false, UnusedFiles = unusedFiles, UnusedBytes = unusedBytes };
            }
            var info = await store.Datastore.GetInfoAsync();
            return (object?)new {
                Open = true,
                UnusedFiles = unusedFiles,
                UnusedBytes = unusedBytes,
                // what the background queues still owe, so a rebuild that has just been queued can
                // be watched from the same panel that started it
                TasksQueued = queuedTasks(store.Datastore),
                ActionsNotInState = info.LogActionsNotItInStatefile,
                TransactionsNotInState = info.LogTransactionsNotItInStatefile,
                TruncatableActions = info.LogTruncatableActions,
                LogFileSize = info.LogFileSize,
                StateFileSize = info.LogStateFileSize,
                RunningRewrite = info.RunningRewriteFile,
            };
        });
        // Re-extracts the search text of every text indexed node and writes it back, which is what
        // rebuilds the index. One background task per node, so this returns once they are queued -
        // the queue count in db-maintenance-info is what says when the work is done.
        Commands.Register("db-rebuild-text-index", ctx => {
            var p = ctx.Payload<IoListPayload>();
            var store = getContainer(p.StoreId).Store ?? throw new Exception("The database must be open. ");
            if (store.State != DataStoreState.Open) throw new Exception("The database must be open. ");
            return (object?)new { Queued = store.Datastore.ReIndexAllText() };
        });
        Commands.Register("db-delete-unused", ctx => {
            var p = ctx.Payload<IoListPayload>();
            var c = getContainer(p.StoreId);
            var io = _server.GetIO(c.Settings.IoDatabase ?? throw new Exception("No database IO provider configured. "));
            var latest = FileKeyUtility.WAL_GetLatestFileKey(io);
            long deleted = 0, freed = 0;
            var errors = new List<string>();
            foreach (var key in FileKeyUtility.WAL_GetAllFileKeys(io)) {
                if (key.IsSameKey(latest)) continue;
                try {
                    var size = io.GetFileSizeOrZeroIfUnknown(key);
                    io.DeleteFileIfItExists(key);
                    deleted++;
                    freed += size;
                } catch (Exception error) {
                    errors.Add(key.AsKeyString() + ": " + error.Message);
                }
            }
            return (object?)new { Deleted = deleted, Freed = freed, Errors = errors };
        });
        Commands.Register("db-truncate", async ctx => {
            var p = ctx.Payload<TruncatePayload>();
            var c = getContainer(p.StoreId);
            var store = c.Store ?? throw new Exception("The database must be open. ");
            if (store.State != DataStoreState.Open) throw new Exception("The database must be open. ");
            var options = MaintenanceAction.TruncateLog;
            if (!p.KeepOld) options |= MaintenanceAction.DeleteOldLogs;
            await store.MaintenanceAsync(options);
            return (object?)new { Done = true };
        });
        Commands.Register("db-save-state", ctx => {
            var p = ctx.Payload<IoListPayload>();
            var c = getContainer(p.StoreId);
            var store = c.Store ?? throw new Exception("The database must be open. ");
            if (store.State != DataStoreState.Open) throw new Exception("The database must be open. ");
            store.Datastore.SaveIndexStates();
            return (object?)new { Done = true };
        });
        // the database is a single WAL file; downloading it is a copy of the database,
        // uploading one (as the next file key, with the state file cleared) is a restore
        Commands.Register("db-file-info", ctx => {
            var p = ctx.Payload<IoListPayload>();
            var c = getContainer(p.StoreId);
            var ioId = c.Settings.IoDatabase ?? throw new Exception("No database IO provider configured. ");
            var io = _server.GetIO(ioId);
            var current = FileKeyUtility.WAL_GetLatestFileKey(io);
            return (object?)new {
                IoId = ioId,
                CurrentKey = current.AsKeyString(),
                NextKey = FileKeyUtility.WAL_NextFileKey(io).AsKeyString(),
                Size = io.GetFileSizeOrZeroIfUnknown(current),
                State = c.HasFailed ? "Error" : c.Store?.State.ToString() ?? "Closed",
                CanUpload = c.Store == null || c.Store.State == DataStoreState.Closed,
            };
        });
        Commands.Register("db-upload-finalize", ctx => {
            var p = ctx.Payload<IoListPayload>();
            var c = getContainer(p.StoreId);
            if (c.IsOpenOrOpening()) throw new Exception("The database must be closed before uploading. ");
            var ioId = c.Settings.IoDatabase ?? throw new Exception("No database IO provider configured. ");
            // an old state file must never be paired with a newer log file
            FileKeyUtility.State_DeleteAll(_server.GetIO(ioId));
            return (object?)new { Done = true };
        });
        // ---- the converted file cache: the resized images and transcoded media the conversion
        // engine derives from stored files. Everything in it is rebuilt on demand, so deleting it
        // costs nothing but the work of converting again. Measuring walks the whole tree, so it is
        // asked for on demand rather than reported with the rest of the maintenance numbers.
        Commands.Register("db-converted-info", async ctx => {
            var p = ctx.Payload<IoListPayload>();
            var (io, folder) = convertedCache(getContainer(p.StoreId));
            var (files, bytes) = await folderTotals(io, folder);
            return (object?)new { Files = files, Bytes = bytes };
        });
        Commands.Register("db-delete-converted", async ctx => {
            var p = ctx.Payload<IoListPayload>();
            var c = getContainer(p.StoreId);
            var store = c.Store ?? throw new Exception("The database must be open. ");
            if (store.State != DataStoreState.Open) throw new Exception("The database must be open. ");
            var (io, folder) = convertedCache(c);
            var (filesBefore, bytesBefore) = await folderTotals(io, folder);
            // the store's own call, so the engine drops its in memory copies of the small files too
            store.Datastore.ClearAllCachedConversions();
            var (filesAfter, bytesAfter) = await folderTotals(io, folder);
            return (object?)new { Deleted = filesBefore - filesAfter, Freed = bytesBefore - bytesAfter, Remaining = filesAfter };
        });
        // Where a database keeps its uploaded files, so the UI can download a file storage the way
        // it downloads a storage folder. Read from the settings rather than the open store, so the
        // list is there while the database is closed as well. A MultiFile store is one folder in
        // its IO provider; a SingleFile store is a file at the provider root instead, so its file
        // keys travel along and the client downloads those.
        Commands.Register("file-store-list", ctx => {
            var p = ctx.Payload<IoListPayload>();
            var c = getContainer(p.StoreId);
            var found = new List<(Guid Id, Guid IoId, FileStoreEngine Type, bool IsDefault)>();
            void add(Guid id, Guid ioId, FileStoreEngine type, bool isDefault) {
                // stores of the same type on the same provider keep their files in the same place,
                // so the implicit default next to an identical configured one is one storage, not two
                var existing = found.FindIndex(f => f.IoId == ioId && f.Type == type);
                if (existing >= 0) {
                    if (isDefault) found[existing] = found[existing] with { IsDefault = true };
                    return;
                }
                found.Add((id, ioId, type, isDefault));
            }
            var configured = c.Settings.FileStoreSettings ?? [];
            var defaultId = c.Settings.LocalSettings?.DefaultFileStore;
            foreach (var fs in configured) add(fs.Id, fs.IoProviderId, fs.StoreType, defaultId == fs.Id);
            // the database falls back to an implicit default file store on its own IO provider
            // whenever no configured store is named as the default one
            if (!configured.Any(fs => fs.Id == defaultId) && c.Settings.IoDatabase is Guid dbIo && dbIo != Guid.Empty)
                add(Guid.Empty, dbIo, FileStoreEngine.MultiFile, true); // the implicit store is always MultiFile
            var ioNames = (c.Settings.IOSettings ?? []).ToDictionary(s => s.Id, s => string.IsNullOrEmpty(s.Name) ? s.IOType.ToString() : s.Name);
            return (object?)found.Select(store => {
                var io = _server.GetIO(store.IoId);
                var files = new List<object>();
                if (store.Type == FileStoreEngine.SingleFile) {
                    foreach (var key in FileKeyUtility.FileStore_GetAllFileKeys(io))
                        files.Add(new { Key = key.AsKeyString(), Size = io.GetFileSizeOrZeroIfUnknown(key) });
                }
                return (object)new {
                    store.Id,
                    Name = ioNames.TryGetValue(store.IoId, out var ioName) ? ioName : store.IoId.ToString(),
                    store.IoId,
                    Type = store.Type.ToString(),
                    Folder = store.Type == FileStoreEngine.MultiFile ? FileKeyUtility.MultiFileStoreFolderKey : null,
                    Files = files,
                    store.IsDefault,
                };
            }).ToList();
        });
        // ---- file store audits: unreferenced files (files no node points at anymore) and the
        // reverse, missing files (file values whose file is gone from the store). Both walk every
        // node, so they run as background jobs the UI polls and can cancel. The job registry is
        // shared with the old admin UI, so the same scan cannot run twice on one database.
        Commands.Register("files-scan-start", ctx => {
            var p = ctx.Payload<FileScanStartPayload>();
            var store = getContainer(p.StoreId).Store ?? throw new Exception("The database must be open. ");
            if (store.State != DataStoreState.Open) throw new Exception("The database must be open. ");
            if (store.Datastore is not DataStoreLocal local) throw new Exception("Only supported for local databases. ");
            var job = p.Scan switch {
                "unreferenced" => FileScanJobs.Start(p.StoreId, "unreferenced files", async j =>
                    (object)await local.DeleteUnreferencedFilesAsync(p.CountOnly, j.SetProgress, j.Cancellation.Token)),
                "missing" => FileScanJobs.Start(p.StoreId, "missing files", async j =>
                    (object)await local.FindMissingFilesAsync(j.SetProgress, j.Cancellation.Token)),
                _ => throw new Exception("Unknown file scan: " + p.Scan),
            };
            return (object?)new { JobId = job.Id };
        });
        Commands.Register("files-scan-progress", ctx => {
            var p = ctx.Payload<FileScanJobPayload>();
            var job = FileScanJobs.Get(p.JobId);
            // the results are only set once the job is done, so the (potentially long) missing
            // file list travels once instead of on every poll
            return (object?)new {
                job.State,
                job.Description,
                job.Percent,
                job.Error,
                Unreferenced = job.Result as DataStores.Files.DeleteUnReferenceResult,
                Missing = job.Result as DataStores.Files.MissingFilesResult,
            };
        });
        Commands.Register("files-scan-cancel", ctx => {
            var p = ctx.Payload<FileScanJobPayload>();
            FileScanJobs.Get(p.JobId).Cancellation.Cancel();
            return (object?)new { Cancelled = true };
        });
        // ---- file conversions: the queue behind image resizing, format conversion and text extraction ----
        // Current holds what is running and queued plus a short tail of finished ones, which is what
        // makes the page useful: a conversion that failed is the one you came looking for.
        Commands.Register("conversions", ctx => {
            var p = ctx.Payload<IoListPayload>();
            var container = getContainer(p.StoreId);
            if (!container.IsOpen()) return (object?)new { Open = false, Running = 0, Queued = 0, Completed = 0, Failed = 0, Canceled = 0, Current = Array.Empty<object>() };
            var conversions = container.Store!.Datastore.GetConversions();
            var datamodel = container.Datamodel;
            return (object?)new {
                Open = true,
                conversions.Running,
                conversions.Queued,
                conversions.Completed,
                conversions.Failed,
                conversions.Canceled,
                Current = conversions.Current.Select(c => new {
                    c.Id,
                    c.FileName,
                    From = c.FromFormat.ToString(),
                    To = c.ToFormat.ToString(),
                    FromType = c.FromType.ToString(),
                    ToType = c.ToType.ToString(),
                    Property = propertyName(datamodel, c.Property),
                    Status = c.Status.ToString(),
                    c.ProgressPercentage,
                    c.Created,
                    c.Started,
                    c.Ended,
                    c.ProcessedMs,
                    c.Description,
                }),
            };
        });
        // Cancelling only stops this run; the next request for the same file starts it over. Cancelling
        // permanently records the failure against the file, so it is not attempted again either.
        Commands.Register("conversion-cancel", async ctx => {
            var p = ctx.Payload<ConversionCancelPayload>();
            var store = getContainer(p.StoreId).Store ?? throw new Exception("The database must be open. ");
            await store.Datastore.CancelConversion(p.Id, p.Permanently);
            return (object?)new { Cancelled = true };
        });
        Commands.Register("store-open", ctx => {
            var p = ctx.Payload<IoListPayload>();
            getContainer(p.StoreId).Open();
            return (object?)new { Done = true };
        });
        Commands.Register("store-close", ctx => {
            var p = ctx.Payload<IoListPayload>();
            getContainer(p.StoreId).CloseIfOpen();
            if (!_server.GetContainers().Any(c => c.IsOpenOrOpening())) _server.ResetIOProviders();
            return (object?)new { Done = true };
        });
        Commands.Register("collect-garbage", ctx => {
            static string mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("N1") + " MB";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var managedBefore = GC.GetTotalMemory(false);
            // the deepest collection available: aggressive reclaims as much as possible (all generations,
            // compacts large object heap, returns memory to the OS), finalizers run, then a second
            // compacting pass picks up what finalization released
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            var managedAfter = GC.GetTotalMemory(false);
            watch.Stop();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return (object?)new {
                Started = true,
                Message = $"Collected in {watch.ElapsedMilliseconds:N0} ms. Managed {mb(managedBefore)} → {mb(managedAfter)}, working set {mb(process.WorkingSet64)}.",
            };
        });
        Commands.Register("soft-restart", ctx => {
            if (!_server.GetRestartCapabilities().CanSoftRestart) throw new Exception("Soft restart is not allowed on this server. ");
            if (_server.IsRestarting) return (object?)new { Started = false, Message = "A restart is already running." };
            // closing and rebuilding every database takes far longer than a request should, so this only
            // starts it: the UI watches the stream drop and reconnect
            _ = Task.Run(async () => {
                try { await _server.SoftRestartAsync(); } catch { } // SoftRestartAsync has already logged it
            });
            return (object?)new { Started = true, Message = "Soft restart started." };
        });
        Commands.Register("stop-host", ctx => {
            if (!_server.GetRestartCapabilities().CanStopHost) throw new Exception("Stopping the host is not allowed on this server. ");
            var started = _server.StopHost();
            return (object?)new { Started = started, Message = started ? "The host is stopping." : "This host cannot be stopped from here." };
        });
    }
}
sealed record IoListPayload(Guid StoreId);
sealed record BackupNowPayload(Guid StoreId, bool Truncate, bool KeepForever);
sealed record BackupRestorePayload(Guid StoreId, string Key);
sealed record TruncatePayload(Guid StoreId, bool KeepOld);
sealed record IoFolderPayload(Guid IoId, string? Path, bool Recursive = false);
sealed record ZipRequestPayload(Guid IoId, string[] Keys, string? BasePath);
sealed record IoDeleteFilesPayload(Guid IoId, string[] Keys);
sealed record FileScanStartPayload(Guid StoreId, string Scan, bool CountOnly);
sealed record FileScanJobPayload(Guid JobId);
sealed record ConversionCancelPayload(Guid StoreId, Guid Id, bool Permanently);
