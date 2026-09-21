using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Stores;
using Relatude.DB.FileConversion;
using Relatude.DB.IO;
using Relatude.DB.NodeServer.API;
using Relatude.DB.NodeServer.Json;
using Relatude.DB.Web;
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
    readonly UILogs _logs;
    readonly UIFileTransfer _transfer;
    string? _lastContainersJson;
    public UIEventStream Events { get; } = new();
    public UICommands Commands { get; }
    /// <summary>What the pages follow, sampled here and pushed on the stream rather than polled.</summary>
    public UILiveFeeds Feeds { get; }
    internal UIServer(RelatudeDBServer server) {
        _server = server;
        Commands = new UICommands(server);
        registerBuiltInCommands();
        new UISettings(server).Register(Commands);
        _logs = new UILogs(server);
        _logs.Register(Commands);
        _transfer = new UIFileTransfer(server);
        new UIDashboard(server).Register(Commands);
        new UIMemory(server).Register(Commands);
        new UITasks(server).Register(Commands);
        new UIDemo(server).Register(Commands);
        new UIWikiImport(server).Register(Commands);
        new UIRevert(server).Register(Commands);
        new UIDatamodel(server).Register(Commands);
        new UIDatabases(server).Register(Commands);
        _query = new UIQuery(server);
        _query.Register(Commands);
        new UISearch(server, _query).Register(Commands);
        // registered last: it runs the commands above on behalf of a connected tab
        Feeds = new UILiveFeeds(server, Commands, Events);
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
            object? revertWindow = null;
            if (c.IsOpen()) {
                // whether a revert window is open rides the broadcast too: it may have been begun
                // from code or the CLI, and every page has to say so the moment it is (UIRevert.cs)
                try {
                    if (c.Store!.Datastore.RevertWindow is RevertWindowInfo w) {
                        revertWindow = new { Timestamp = w.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture), w.BegunUtc };
                    }
                } catch { }
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
                RevertWindow = revertWindow,
            };
        })];
    }
    internal void Map(WebApplication app) {
        var path = _server.ApiUrlRoot + "/ui/";
        app.MapGet(path + "stream", Events.Connect);
        app.MapPost(path + "command", (Delegate)Commands.Execute); // Delegate overload, so the returned IResult is written to the response
        _transfer.Map(app, path); // uploads and batched downloads (binary, so not commands): see UIFileTransfer
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
        // the pictures of the cards on the visual pivot's screen, at one width, streamed back as a
        // sequence of records as each comes ready (binary, so not a command): see UIQuery.WriteCardImages
        app.MapPost(path + "card-images", async (HttpContext ctx, UIQuery.CardImagesPayload payload) => {
            try {
                await _query.WriteCardImages(ctx, payload);
            } catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) {
                // the browser moved on: the pictures it no longer wants are simply not sent
            } catch (Exception error) when (!ctx.Response.HasStarted) {
                return Results.Json(new { error = error.Message }, RelatudeDBJsonOptions.Default, statusCode: 500);
            }
            return Results.Empty;
        });
        // a log as a tab separated file (a download, so not a command): the whole log, or the range
        // the logs page is showing
        app.MapPost(path + "log-tsv", async (HttpContext ctx, UILogs.ExportPayload payload) => {
            try {
                await _logs.WriteTsv(ctx, payload);
            } catch (Exception error) when (!ctx.Response.HasStarted) {
                // the log and its range are read before the first row is written, so until then the
                // client can still be told what went wrong
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
        // the file an older version of a node held, as a download, for the history tab of the node
        // form: "t" is the version's log timestamp and "p" the file property on it
        app.MapGet(path + "version-file", async (HttpContext ctx, Guid storeId, Guid id, long t, Guid p, int? max) => {
            try {
                return await _query.WriteVersionFile(ctx, storeId, id, t, p, max ?? 50);
            } catch (Exception error) when (!ctx.Response.HasStarted) {
                return Results.Json(new { error = error.Message }, RelatudeDBJsonOptions.Default, statusCode: 500);
            }
        });
        // the file itself, inline with its own content type, for the viewer panel of the files section:
        // an <img>, a <video> (ranges) or a fetch of the text going into the editor point at it
        app.MapGet(path + "file", async (HttpContext ctx, Guid ioId, string key) => {
            try {
                return await serveFileAsync(ctx, ioId, key);
            } catch (Exception error) when (!ctx.Response.HasStarted) {
                return Results.Json(new { error = error.Message }, RelatudeDBJsonOptions.Default, statusCode: 500);
            }
        });
        // a small picture of a file, for the thumbnail view of the files section: the image scaled
        // down to the tile, so browsing a folder of photographs never pulls the originals across.
        // A format no converter reads answers 415 and the tile shows its file type icon instead
        app.MapGet(path + "thumb", async (HttpContext ctx, Guid ioId, string key, int? w) => {
            try {
                return await serveThumbnailAsync(ctx, ioId, key, w ?? 256);
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
    async Task<IResult> serveFileAsync(HttpContext ctx, Guid ioId, string key) {
        var io = _server.GetIO(ioId);
        var fileKey = key.SplitKey();
        if (fileKey.Length == 0) return Results.BadRequest(new { error = "No file given. " });
        Stream? stream;
        try {
            stream = OpenFileForReading(io, fileKey);
        } catch (IOException) {
            return Results.StatusCode(StatusCodes.Status423Locked);
        }
        if (stream == null) return Results.NotFound();
        return await FileHandler.HandleFileAsync(ctx, stream, fileKey.FileName(), false, contentTypeOf(fileKey.FileName()), false);
    }
    // ---- thumbnails ----
    // Decoding an image costs a core and holds the whole picture in memory, and a thumbnail grid
    // asks for every tile it shows at once, so no more than this many are made at a time.
    readonly SemaphoreSlim _thumbnailWork = new(Math.Max(2, Environment.ProcessorCount / 2));
    // the built in converter is always there; a host that registered Skia (or anything else) gets
    // the formats that one adds on top
    static readonly NativeImageConverter _nativeImages = new();
    const int maxThumbnailSourceBytes = 64 * 1024 * 1024; // an "image" bigger than this is not decoded

    /// <summary>
    /// The image at the key, scaled to fit a tile of the given width. Only images are answered:
    /// anything else - a video, a document, an image format no converter reads - is a 415, which the
    /// client shows as the file's type icon. Vector images are sent as they are: they are small, and
    /// nothing is gained by rasterising one.
    /// </summary>
    async Task<IResult> serveThumbnailAsync(HttpContext ctx, Guid ioId, string key, int width) {
        width = Math.Clamp(width, 16, 1024);
        var io = _server.GetIO(ioId);
        var fileKey = key.SplitKey();
        if (fileKey.Length == 0) return Results.BadRequest(new { error = "No file given. " });
        var format = FileFormatUtil.GetDetailedFormat(fileKey.FileName());
        if (FileFormatUtil.GetFileType(format) != FileType.Image) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (format == FileFormat.Svg) return await serveFileAsync(ctx, ioId, key); // the browser draws it better than any resize would
        if (!tryGetImageConverter(format, out var converter, out var outFormat)) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (io.GetFileSizeOrZeroIfUnknown(fileKey) > maxThumbnailSourceBytes) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        byte[] bytes;
        await _thumbnailWork.WaitAsync(ctx.RequestAborted);
        try {
            var opened = OpenFileForReading(io, fileKey);
            if (opened == null) return Results.NotFound();
            using var stream = opened;
            using var image = converter.Load(stream);
            // a picture already smaller than the tile is only re-encoded, never blown up
            var scaled = image.Width > width ? image.Adjust(new FileAdjustmentImage { Width = width, CropMode = ImageCropMode.Fit }) : image;
            try {
                bytes = scaled.Encode(outFormat, 80);
            } finally {
                if (!ReferenceEquals(scaled, image)) scaled.Dispose();
            }
        } catch (IOException) {
            return Results.StatusCode(StatusCodes.Status423Locked);
        } catch (Exception) {
            // a picture the decoder cannot make sense of is not an error worth a 500: the tile
            // falls back to the file type icon exactly as it does for a format nothing reads
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        } finally {
            _thumbnailWork.Release();
        }
        // the url carries the file's modified time, so a thumbnail can be kept for as long as the
        // browser likes: a replaced file is a different url
        ctx.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.Bytes(bytes, FileFormatUtil.GetContentType(outFormat));
    }

    /// <summary>
    /// A converter that reads the format, and what it should write. Png keeps transparency, which a
    /// thumbnail of an icon or a logo needs; everything else is jpeg, which is far smaller.
    /// </summary>
    bool tryGetImageConverter(FileFormat format, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ImageConverterBase? converter, out FileFormat outFormat) {
        outFormat = format is FileFormat.Png or FileFormat.Gif or FileFormat.Webp ? FileFormat.Png : FileFormat.Jpeg;
        foreach (var candidate in _server.Options?.FileConverters ?? []) {
            if (candidate is ImageConverterBase image && image.SupportsConversion(format, outFormat)) {
                converter = image;
                return true;
            }
        }
        if (_nativeImages.SupportsConversion(format, outFormat)) {
            converter = _nativeImages;
            return true;
        }
        converter = null;
        return false;
    }

    /// <summary>
    /// The file's bytes. Disk backed files are opened directly, rather than through the provider,
    /// because its OpenRead retries an OS locked file for minutes while holding the provider lock.
    /// The wrapped provider stream is seekable, so a video player's range requests work against
    /// either. Null when the file is gone; an IOException means it is locked.
    /// <para><paramref name="shareWithWriters"/> is what a viewer wants and a copy does not: showing
    /// a log file while it is being written is the point of the viewer, whereas writing that same
    /// half state to disk under the file's own name hands over a copy that is quietly wrong. A
    /// download passes false and gets the IOException instead.</para>
    /// </summary>
    internal static Stream? OpenFileForReading(IIOProvider io, string[] fileKey, bool shareWithWriters = true) {
        if (io.TryGetLocalFilePath(fileKey, out var localFilePath)) {
            try {
                var share = shareWithWriters ? FileShare.ReadWrite | FileShare.Delete : FileShare.Read;
                return new FileStream(localFilePath, FileMode.Open, FileAccess.Read, share, 64 * 1024, useAsync: true);
            } catch (FileNotFoundException) {
                return null;
            } catch (DirectoryNotFoundException) {
                return null;
            }
        }
        if (!io.Exists(fileKey)) return null;
        return ReadStreamWrapper.Wrap(io.OpenRead(fileKey, 0));
    }

    static readonly Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider _contentTypes = new();
    // the code and config files a project folder holds, which the standard map either lacks or gets
    // wrong for this purpose (.ts is a video format there); everything text is served as utf-8
    static readonly Dictionary<string, string> _textContentTypes = new(StringComparer.OrdinalIgnoreCase) {
        [".ts"] = "text/plain", [".tsx"] = "text/plain", [".jsx"] = "text/plain", [".mjs"] = "text/javascript", [".cjs"] = "text/javascript",
        [".cs"] = "text/plain", [".razor"] = "text/plain", [".cshtml"] = "text/plain", [".csproj"] = "text/xml", [".props"] = "text/xml",
        [".targets"] = "text/xml", [".slnx"] = "text/xml", [".md"] = "text/markdown", [".yml"] = "text/plain", [".yaml"] = "text/plain",
        [".toml"] = "text/plain", [".ini"] = "text/plain", [".log"] = "text/plain", [".env"] = "text/plain", [".gitignore"] = "text/plain",
        [".editorconfig"] = "text/plain", [".sql"] = "text/plain", [".ps1"] = "text/plain", [".sh"] = "text/plain", [".bat"] = "text/plain",
        [".cmd"] = "text/plain", [".json"] = "application/json", [".map"] = "application/json", [".svg"] = "image/svg+xml",
    };
    static string contentTypeOf(string fileName) {
        var extension = Path.GetExtension(fileName);
        if (!_textContentTypes.TryGetValue(extension, out var contentType) && !_contentTypes.TryGetContentType(fileName, out contentType)) {
            return "application/octet-stream";
        }
        var isText = contentType.StartsWith("text/") || contentType == "application/json" || contentType == "application/javascript" || contentType == "application/xml";
        return isText ? contentType + "; charset=utf-8" : contentType;
    }
    // what a key segment may look like depends on the provider: database storage keeps to the
    // strict file key alphabet, the project folder takes any legal file system name
    internal static bool IsValidSegment(IIOProvider io, string segment) => io is IOProviderDisk disk ? disk.IsValidKeySegment(segment) : FileKeyUtility.IsFileKeyValid(segment);
    static string validName(IIOProvider io, string? name) {
        name = name?.Trim() ?? "";
        if (name.Length == 0) throw new Exception("The name cannot be empty. ");
        if (name.Contains('/') || name.Contains('\\')) throw new Exception("The name cannot contain path separators. ");
        if (!IsValidSegment(io, name)) {
            throw new Exception(io is IOProviderDisk { PlainFolder: true }
                ? "The name is not a legal file system name. "
                : "Names in database storage can only contain letters, numbers, dash, space, underscore, dot and parentheses, and be at most " + FileKeyUtility.MaxFileNameLength + " characters. ");
        }
        return name;
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
        // Whatever else the bundler split out of the page (see vite.config.ts): the pictures of the
        // Earth the globe can wear are a megabyte, and are a chunk of their own precisely so
        // that nobody downloads them until they ask for one. The page asks for such a chunk by name,
        // beside the script it was loaded from, so that is where they are served - by name rather
        // than by hash, since the name is what the bundle already has written into it. That means
        // they cannot be cached for ever the way the page itself is: they are revalidated instead,
        // which for an unchanged chunk is a header and no body at all.
        foreach (var name in ServerAPIMapper.ResourceNames("ClientUI.")) {
            if (!name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || name == "index.js") continue;
            var chunk = ServerAPIMapper.GetResource("ClientUI." + name);
            var tag = "\"" + chunk.XXH64Hash().ToString("x") + "\"";
            app.MapGet(files + name, (HttpContext ctx) => {
                if (ctx.Request.Headers.IfNoneMatch.Contains(tag)) {
                    ctx.Response.StatusCode = StatusCodes.Status304NotModified;
                    return Results.Empty;
                }
                ctx.Response.ContentType = "text/javascript";
                ctx.Response.Headers.ETag = tag;
                ctx.Response.Headers.CacheControl = "public, max-age=0, must-revalidate";
                return Results.Text(chunk, "text/javascript");
            });
        }

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
    // An engine folder is named by the engine's id, and several engines of the same type can sit in
    // the same list, so the role (and a number, when there is more than one) keeps them apart.
    static void addEngineNames(Dictionary<string, string> map, DataStores.IndexEngineSettings[]? engines, string role) {
        if (engines == null) return;
        for (var i = 0; i < engines.Length; i++) {
            if (engines[i].Id == Guid.Empty) continue;
            var name = role + "-" + (engines[i].TypeName ?? "index").ToLowerInvariant();
            map[engines[i].Id.ToString("N")] = engines.Length > 1 ? name + "-" + (i + 1) : name;
        }
    }
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
    /// <summary>A UTC time the way the admin UI reads it back: an ISO string that says it is UTC.
    /// A zero tick count is how the store reports "no such moment" (an empty log has no first and no
    /// last transaction), and it reaches the page as nothing rather than as the year 1.</summary>
    static string? utc(DateTime? value) => value is DateTime v && v.Ticks > 0 ? DateTime.SpecifyKind(v, DateTimeKind.Utc).ToString("o") : null;

    // ---- putting a different file in place as the database ----
    // Restoring a backup, going back in time, adopting a file from the Files page and uploading one
    // are the same operation with four sources: a new log file is written next to the current one
    // and the database is opened on it. The current file is never touched - it stays one file key
    // behind, which is what makes every one of them reversible (by adopting it again).

    /// <summary>
    /// Writes a new write ahead log file and drops everything derived from the one it replaces, so
    /// the next open reads the new file and rebuilds around it. Returns the key it was written to.
    /// The database must be closed.
    /// <para><paramref name="write"/> is handed the database's IO provider and the key to write, and
    /// the key is worked out here rather than by the caller on purpose: one picked before the
    /// database was closed can have become the live file in the meantime, and writing onto that
    /// would destroy the database instead of replacing it.</para>
    /// </summary>
    string[] switchToLogFile(NodeStoreContainer c, Action<IIOProvider, string[]> write) {
        if (c.IsOpenOrOpening()) throw new Exception("The database must be closed first. ");
        var dbIo = _server.GetIO(c.Settings.IoDatabase ?? throw new Exception("No database IO provider configured. "));
        var destKey = FileKeyUtility.WAL_NextFileKey(dbIo);
        if (!dbIo.DoesNotExistOrIsEmpty(destKey)) throw new Exception("The database file " + destKey.AsKeyString() + " already exists. ");
        write(dbIo, destKey);
        try {
            LogFileScan.ReadHeader(dbIo, destKey); // a file the database cannot open must never be left in its place
        } catch {
            dbIo.DeleteFileIfItExists(destKey);
            throw;
        }
        clearLogDerivedFiles(c, dbIo);
        return destKey;
    }

    /// <summary>
    /// Deletes everything built from the log file being replaced: the state snapshot, the state
    /// files of the memory indexes, and the secondary log (recreated from the new primary at the
    /// next open). The index engines are not touched here - they reset themselves on seeing a log
    /// file id they do not know, which is why every writer above stamps a fresh one.
    /// </summary>
    void clearLogDerivedFiles(NodeStoreContainer c, IIOProvider dbIo) {
        // the state files live with the index provider when there is one, which is not necessarily
        // the database provider
        foreach (var io in new[] { dbIo, _server.GetOrNullIO(c.Settings.IoIndexes) }.OfType<IIOProvider>().Distinct()) {
            FileKeyUtility.State_DeleteAll(io);
            foreach (var key in FileKeyUtility.Index_GetAll(io)) io.DeleteFileIfItExists(key);
        }
        if (c.Settings.LocalSettings?.SecondaryBackupLog == true) {
            var secondaryIo = _server.GetOrNullIO(c.Settings.IoDatabaseSecondary) ?? dbIo;
            secondaryIo.DeleteFileIfItExists(FileKeyUtility.WAL_GetSecondaryFileKey());
        }
    }

    // ---- finding log files to go back in ----

    /// <summary>Every storage a database log file could be lying in: the providers of the database's
    /// own settings, and the website project folder the Files page lists as well. Named the way that
    /// page names them, so a file found here is one the reader can go and look at.</summary>
    List<(Guid Id, string Name)> logFileProviders(NodeStoreContainer c) {
        var providers = (c.Settings.IOSettings ?? [])
            .Select(io => (io.Id, Name: string.IsNullOrEmpty(io.Name) ? io.IOType.ToString() : io.Name))
            .DistinctBy(provider => provider.Id).ToList();
        if (!providers.Any(provider => provider.Id == RelatudeDBServer.ProjectRootIOId))
            providers.Add((RelatudeDBServer.ProjectRootIOId, "[Server root]"));
        return providers;
    }

    /// <summary>One log file as the dialog lists it. The header is read for the moment the file
    /// begins at, which is what tells two copies of a database apart at a glance - except on the file
    /// a running database is holding, where there is nothing to read and nothing wrong with that
    /// (<see cref="isLiveLogFile"/>); the dialog fills that one in from the database itself. A file
    /// that cannot be read is listed with what went wrong rather than left out: a backup that has
    /// gone bad is exactly what somebody looking for one wants to be told.</summary>
    object describeLogFile(IIOProvider io, Guid ioId, string ioName, string[] key, long size, DateTime? modifiedUtc,
        bool isBackup, bool isCurrent, NodeStoreContainer c) {
        var inUse = isCurrent && c.Store != null && c.Store.State == DataStoreState.Open;
        string? firstChangeUtc = null, fileId = null, error = null;
        if (!inUse) {
            try {
                var header = LogFileScan.ReadHeader(io, key);
                firstChangeUtc = utc(header.FirstUtc);
                fileId = header.FileId.ToString();
            } catch (Exception e) {
                error = e.Message;
            }
        }
        return new {
            IoId = ioId,
            IoName = ioName,
            Key = key.AsKeyString(),
            Name = key.FileName(),
            Folder = key.Length > 1 ? key[0] : "",
            Size = size,
            ModifiedUtc = utc(modifiedUtc),
            IsCurrent = isCurrent,
            InUse = inUse,
            BackupUtc = isBackup ? utc(backupTimeOrNull(key)) : null,
            KeepForever = isBackup && FileKeyUtility.WAL_KeepForever(key),
            FirstChangeUtc = firstChangeUtc,
            FileId = fileId,
            Error = error,
        };
    }

    /// <summary>The moment in a backup's file name. A name that does not parse is not an error worth
    /// refusing the file over - it is only what the list is sorted by.</summary>
    static DateTime? backupTimeOrNull(string[] key) {
        try {
            return FileKeyUtility.WAL_GetBackUpDateTimeFromFileKey(key);
        } catch {
            return null;
        }
    }
    static DateTime backupTimeOrDefault(string[] key) => backupTimeOrNull(key) ?? DateTime.MinValue;

    /// <summary>Whether this is the log file a running database is holding. It keeps it to itself
    /// (FileShare.None), so nothing can read the file until the database is closed.</summary>
    bool isLiveLogFile(NodeStoreContainer c, Guid ioId, string[] key) {
        if (c.Store == null || c.Store.State != DataStoreState.Open) return false;
        if (c.Settings.IoDatabase is not Guid dbIoId || dbIoId != ioId) return false;
        return key.IsSameKey(FileKeyUtility.WAL_GetLatestFileKey(_server.GetIO(dbIoId)));
    }

    /// <summary>The database's current log file, with the provider it lives in. Throws when there is
    /// nothing to read - every operation below copies from it or replaces it.</summary>
    (IIOProvider Io, string[] Key) currentLogFile(NodeStoreContainer c) {
        var io = _server.GetIO(c.Settings.IoDatabase ?? throw new Exception("No database IO provider configured. "));
        var key = FileKeyUtility.WAL_GetLatestFileKey(io);
        if (io.DoesNotExistOrIsEmpty(key)) throw new Exception("There is no database file to read. ");
        return (io, key);
    }

    /// <summary>The file id of the log being replaced, or null when there is no readable one - an
    /// installation that has never been opened has no database file at all, and a file that cannot
    /// be read is one nothing can be said to collide with.</summary>
    Guid? currentLogFileId(NodeStoreContainer c) {
        try {
            var (io, key) = currentLogFile(c);
            return LogFileScan.ReadHeader(io, key).FileId;
        } catch {
            return null;
        }
    }
    // ---- the drives the server writes to ----

    /// <summary>One drive with server folders on it: what it is, how big, and what put it there.</summary>
    sealed record DriveReading(string Name, string? Label, string? Format, long TotalBytes, long FreeBytes, List<string> Uses);

    /// <summary>The folder of the database the live sample reports on, resolved once.</summary>
    string? _dataFolder;
    bool _dataFolderResolved;

    /// <summary>
    /// Every folder the server has of its own, with the one word saying what it is for. Only local
    /// disk providers have a folder at all - a memory provider has none and a blob container is not
    /// on this machine - so the others are left out rather than guessed at. Nothing here creates a
    /// provider that does not exist yet beyond the plain disk ones, which are an object and no I/O.
    /// </summary>
    IEnumerable<(string Folder, string Use)> serverFolders() {
        var projectRoot = tryFolder(() => _server.ProjectRootIO.BaseFolder);
        if (projectRoot != null) yield return (projectRoot, "website");
        var temp = tryFolder(() => (_server.TempIO as IOProviderDisk)?.BaseFolder);
        if (temp != null) yield return (temp, "temp");
        foreach (var container in _server.Settings.ContainerSettings ?? []) {
            var name = string.IsNullOrEmpty(container.Name) ? container.Id.ToString() : container.Name;
            foreach (var io in container.IOSettings ?? []) {
                if (io.IOType != IOTypes.LocalDisk) continue;
                var folder = tryFolder(() => (_server.GetOrNullIO(io.Id) as IOProviderDisk)?.BaseFolder);
                if (folder != null) yield return (folder, name);
            }
        }
    }

    static string? tryFolder(Func<string?> read) {
        try {
            var folder = read();
            return string.IsNullOrWhiteSpace(folder) ? null : folder;
        } catch {
            return null; // a provider that cannot be built is not a fact worth failing the page for
        }
    }

    /// <summary>Every drive the server writes to, once each, with what puts it there.</summary>
    List<DriveReading> readDisks() {
        var byRoot = new Dictionary<string, DriveReading>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, use) in serverFolders()) {
            var drive = readDrive(folder);
            if (drive == null) continue;
            if (!byRoot.TryGetValue(drive.Name, out var known)) byRoot.Add(drive.Name, known = drive);
            if (!known.Uses.Contains(use)) known.Uses.Add(use);
        }
        return [.. byRoot.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)];
    }

    static DriveReading? readDrive(string folder) {
        try {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0) return null;
            return new DriveReading(drive.Name, safeLabel(drive), drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace, []);
        } catch {
            // an unmapped drive, a share nobody may stat, a path that is not on a drive at all
            return null;
        }
    }

    static string? safeLabel(DriveInfo drive) {
        try { return string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel; } catch { return null; }
    }

    /// <summary>
    /// The drive the databases are on, for the live sample: the default database's folder where
    /// there is one, else the first folder the server has. Resolved once - the folder does not move
    /// under a running server, and this is read on the refresh cadence.
    /// </summary>
    DriveReading? readDataDrive() {
        if (!_dataFolderResolved) {
            _dataFolderResolved = true;
            var folders = serverFolders().ToList();
            var defaultId = _server.Settings.DefaultStoreId;
            var preferred = _server.Settings.ContainerSettings?.FirstOrDefault(c => c.Id == defaultId);
            var name = preferred == null ? null : string.IsNullOrEmpty(preferred.Name) ? preferred.Id.ToString() : preferred.Name;
            _dataFolder = (name == null ? null : folders.FirstOrDefault(f => f.Use == name).Folder)
                ?? folders.FirstOrDefault(f => f.Use != "website" && f.Use != "temp").Folder
                ?? folders.FirstOrDefault().Folder;
        }
        return _dataFolder == null ? null : readDrive(_dataFolder);
    }

    static DateTime? processStartedUtc(System.Diagnostics.Process process) {
        try { return process.StartTime.ToUniversalTime(); } catch { return null; }
    }

    static int? threadCount(System.Diagnostics.Process process) {
        try { return process.Threads.Count; } catch { return null; }
    }

    string? tempFolder() => tryFolder(() => (_server.TempIO as IOProviderDisk)?.BaseFolder);

    void registerBuiltInCommands() {
        Commands.Register("ping", ctx => new { Pong = true, ServerTimeUtc = DateTime.UtcNow });
        // who is looking at this UI, for the footer of the nav rail. Two ways in, and they differ in
        // what "log out" would mean: a token is a session that can be ended, the localhost bypass is
        // not one, so the UI must not offer to end it
        Commands.Register("whoami", ctx => {
            var (userName, viaLocalhost, via) = _server.Authentication.Describe(ctx.Http);
            return new {
                UserName = userName,
                ViaLocalhost = viaLocalhost,
                Via = via, // "master" or "license", null under the bypass
                CanLogOut = userName != null,
                Machine = Environment.MachineName,
            };
        });
        Commands.Register("server-info", ctx => new {
            Version = typeof(UIServer).Assembly.GetName().Version?.ToString(),
            UpTimeMs = _server.UpTime.TotalMilliseconds,
            Containers = buildContainers(),
        });
        // the process alone, cheap enough to read at the refresh rate: what the overview graphs.
        // The data drive rides along - it is one stat() call and it belongs on the same picture:
        // a truncate or a backup that is eating the disk shows up there and nowhere else.
        Commands.Register("server-live", ctx => {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var disk = readDataDrive();
            return (object?)new {
                SampledUtc = DateTime.UtcNow,
                ManagedMemory = GC.GetTotalMemory(false),
                ProcessMemory = process.WorkingSet64,
                ProcessorTimeMs = process.TotalProcessorTime.TotalMilliseconds,
                ProcessorCount = Environment.ProcessorCount,
                DiskTotalBytes = disk?.TotalBytes ?? 0,
                DiskFreeBytes = disk?.FreeBytes ?? 0,
                DiskName = disk?.Name,
            };
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
                // the rest of what the host is, for the facts list: the process, the runtime it is
                // hosted by, and the drives the databases are written to
                ProcessId = Environment.ProcessId,
                ProcessName = process.ProcessName,
                ProcessStartedUtc = processStartedUtc(process),
                ThreadCount = threadCount(process),
                ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                OsArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                ServerGC = System.Runtime.GCSettings.IsServerGC,
                GCMode = System.Runtime.GCSettings.LatencyMode.ToString(),
                // what the runtime believes it may grow to: a container memory limit shows up here
                // and nowhere else, and it is the number a heap graph has to be read against
                MemoryLimitBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                Environment = System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                    ?? System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
                WorkingFolder = System.Environment.CurrentDirectory,
                TempFolder = tempFolder(),
                UtcOffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes,
                TimeZone = TimeZoneInfo.Local.Id,
                ServerTimeUtc = DateTime.UtcNow,
                Disks = readDisks(),
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
        // What the guids inside file and folder names stand for, so the files section can offer to
        // show names instead: property, node type and relation ids (index files and their folders are
        // named after the property they index) and the index engine ids naming the folders below
        // indexes/. Keyed by the "N" form (32 hex, no dashes) - the same id appears both ways.
        Commands.Register("name-map", ctx => {
            var p = ctx.Payload<IoListPayload>();
            var c = getContainer(p.StoreId);
            var map = new Dictionary<string, string>();
            void add(Guid id, string? name) {
                if (id != Guid.Empty && !string.IsNullOrWhiteSpace(name)) map[id.ToString("N")] = name.ToLowerInvariant();
            }
            var datamodel = c.Datamodel;
            if (datamodel != null) {
                foreach (var type in datamodel.NodeTypes.Values) add(type.Id, type.CodeName);
                foreach (var relation in datamodel.Relations.Values) add(relation.Id, relation.CodeName);
                // a property name is only unique within its node type, and the same index folder
                // listing can hold two of them, so the ambiguous ones carry their type
                foreach (var group in datamodel.Properties.Values.GroupBy(prop => prop.CodeName, StringComparer.OrdinalIgnoreCase)) {
                    var ambiguous = group.Count() > 1;
                    foreach (var prop in group) {
                        add(prop.Id, ambiguous && datamodel.NodeTypes.TryGetValue(prop.NodeType, out var owner)
                            ? owner.CodeName + "." + prop.CodeName
                            : prop.CodeName);
                    }
                }
            }
            var local = c.Settings.LocalSettings;
            addEngineNames(map, local?.ValueIndexes, "value");
            addEngineNames(map, local?.TextIndexes, "text");
            addEngineNames(map, local?.VectorIndexes, "vector");
            return (object?)map;
        });
        // the IO providers of one database container, for the files section, plus the one every server
        // has: the website project folder (see RelatudeDBServer.ProjectRootIO). What each can do is
        // told from its type rather than from a live instance, so listing never connects to anything.
        Commands.Register("io-list", ctx => {
            var p = ctx.Payload<IoListPayload>();
            var c = getContainer(p.StoreId);
            var list = (c.Settings.IOSettings ?? []).Select(io => new IoInfo {
                Id = io.Id,
                Name = string.IsNullOrEmpty(io.Name) ? io.IOType.ToString() : io.Name,
                Type = io.IOType.ToString(),
                Kind = "storage",
                CanRenameFile = io.IOType != IOTypes.AzureBlobStorage,
                CanRenameFolder = io.IOType != IOTypes.AzureBlobStorage,
                SupportsEmptyFolders = io.IOType == IOTypes.LocalDisk,
            }).ToList();
            var root = _server.ProjectRootIO;
            list.Add(new IoInfo {
                Id = RelatudeDBServer.ProjectRootIOId,
                Name = "[Server root]",
                Type = IOTypes.LocalDisk.ToString(),
                Kind = "projectRoot",
                CanRenameFile = root.CanRenameFile,
                CanRenameFolder = root.CanRenameFolder,
                SupportsEmptyFolders = root.SupportsEmptyFolders,
                LocalPath = root.BaseFolder,
            });
            return (object?)list;
        });
        // renames one file within its folder; the new name is a single segment. A change of case
        // only is a real rename on a case insensitive file system, so it is not an "already exists"
        Commands.Register("io-rename-file", ctx => {
            var p = ctx.Payload<IoRenamePayload>();
            var io = _server.GetIO(p.IoId);
            if (!io.CanRenameFile) throw new Exception("This IO provider cannot rename files. ");
            var key = p.Key.SplitKey();
            if (key.Length == 0) throw new Exception("No file given. ");
            var newName = validName(io, p.NewName);
            if (newName == key[^1]) return (object?)new { Key = key.AsKeyString() };
            string[] newKey = [.. key[..^1], newName];
            if (!newKey.IsSameKey(key) && io.Exists(newKey)) throw new Exception($"{newKey.AsKeyString()} already exists. ");
            io.RenameFile(key, newKey);
            return (object?)new { Key = newKey.AsKeyString() };
        });
        Commands.Register("io-rename-folder", ctx => {
            var p = ctx.Payload<IoRenamePayload>();
            var io = _server.GetIO(p.IoId);
            if (!io.CanRenameFolder) throw new Exception("This IO provider cannot rename folders. ");
            var folder = splitFolderPath(p.Key);
            if (folder.Length == 0) throw new Exception("The storage root cannot be renamed. ");
            var newName = validName(io, p.NewName);
            if (newName == folder[^1]) return (object?)new { Path = folder.AsKeyString() };
            string[] newFolder = [.. folder[..^1], newName];
            io.RenameFolder(folder, newFolder);
            return (object?)new { Path = newFolder.AsKeyString() };
        });
        // Key is the parent folder ("" = the storage root), NewName the folder to make in it. On a
        // provider with virtual folders nothing is stored: the folder appears with its first file
        Commands.Register("io-create-folder", ctx => {
            var p = ctx.Payload<IoRenamePayload>();
            var io = _server.GetIO(p.IoId);
            var parent = splitFolderPath(p.Key);
            var name = validName(io, p.NewName);
            string[] folder = [.. parent, name];
            io.EnsureFolder(folder);
            return (object?)new { Path = folder.AsKeyString(), Persisted = io.SupportsEmptyFolders };
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
        // and clears everything derived from the old log; the database must be closed, the UI reopens it after
        Commands.Register("backup-restore", ctx => {
            var p = ctx.Payload<BackupRestorePayload>();
            var c = getContainer(p.StoreId);
            var backupIo = _server.GetIO(getBackupIoId(c));
            var sourceKey = p.Key.SplitKey();
            if (backupIo.DoesNotExistOrIsEmpty(sourceKey)) throw new Exception("Backup not found. ");
            var size = backupIo.GetFileSizeOrZeroIfUnknown(sourceKey);
            LogFileScan.ReadHeader(backupIo, sourceKey); // refuses anything that is not a readable log file
            var newKey = switchToLogFile(c, (dbIo, destKey) => LogFileScan.Copy(backupIo, sourceKey, dbIo, destKey, size, Guid.NewGuid()));
            return (object?)new { NewKey = newKey.AsKeyString() };
        });
        // What the "go back in time" dialog offers to go back to: the ends of the log, and the last
        // revert window somebody began (whatever became of it - see RevertMark). Asked for when the
        // dialog opens rather than reported with the rest of the page, because a closed database
        // has to be read off its file, which means walking the log.
        Commands.Register("db-time-travel-info", async ctx => {
            var p = ctx.Payload<IoListPayload>();
            var c = getContainer(p.StoreId);
            var dbIo = _server.GetIO(c.Settings.IoDatabase ?? throw new Exception("No database IO provider configured. "));
            var key = FileKeyUtility.WAL_GetLatestFileKey(dbIo);
            DateTime? first = null, last = null;
            var store = c.Store;
            if (store != null && store.State == DataStoreState.Open) {
                // the running database holds its log file exclusively, so it is the only one that
                // can say where the log begins and ends
                var info = await store.Datastore.GetInfoAsync();
                first = info.LogFirstStateUtc;
                last = info.LogLastChange;
            } else if (!dbIo.DoesNotExistOrIsEmpty(key)) {
                var cut = LogFileScan.Until(dbIo, key, DateTime.MaxValue); // closed: the file is ours to read
                first = cut.FirstUtc;
                last = cut.LastUtc;
            }
            var mark = RevertMark.ReadOrNull(_server.GetOrNullIO(c.Settings.IoIndexes) ?? dbIo);
            return (object?)new {
                CurrentKey = key.AsKeyString(),
                Size = dbIo.GetFileSizeOrZeroIfUnknown(key),
                Open = store != null && store.State == DataStoreState.Open,
                FirstChangeUtc = utc(first),
                LastChangeUtc = utc(last),
                RevertWindowUtc = utc(mark?.Utc),
                RevertWindowBegunUtc = utc(mark?.BegunUtc),
                RevertWindowActive = store?.Datastore.RevertWindow != null,
            };
        });
        // Every database log file the server can see: the one the database is running on, the ones it
        // has moved on from, and every backup of one, in each storage the database has plus the
        // website project folder. This is what the "go back in time" dialog offers to go back in -
        // going back is a copy of a log file cut short, and any log file will do, not only the one in
        // use. IoId/Key name one more file to include, which is how the dialog opened from the Files
        // page carries in a file lying somewhere neither folder below is looked at.
        Commands.Register("db-log-files", ctx => {
            var p = ctx.Payload<LogFilesPayload>();
            var c = getContainer(p.StoreId);
            var dbIoId = c.Settings.IoDatabase;
            var currentKey = dbIoId is Guid dbId ? FileKeyUtility.WAL_GetLatestFileKey(_server.GetIO(dbId)) : null;
            var files = new List<object>();
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var provider in logFileProviders(c)) {
                IIOProvider io;
                FileMeta[] listing;
                try {
                    io = _server.GetIO(provider.Id);
                    listing = io.GetFiles();
                } catch {
                    continue; // a storage that cannot be reached holds nothing the dialog could offer
                }
                var data = new List<object>();
                var backups = new List<(DateTime When, object File)>();
                foreach (var meta in listing) {
                    var key = meta.KeyOf();
                    var isBackup = FileKeyUtility.WAL_IsBackUpFileKey(key);
                    if (!isBackup && !FileKeyUtility.WAL_IsFileKey(key)) continue;
                    listed.Add(provider.Id + "|" + key.AsKeyString());
                    var file = describeLogFile(io, provider.Id, provider.Name, key, meta.Size, meta.LastModifiedUtc,
                        isBackup, provider.Id == dbIoId && key.IsSameKey(currentKey), c);
                    if (isBackup) backups.Add((backupTimeOrDefault(key), file));
                    else data.Add(file);
                }
                files.AddRange(data); // oldest key first: the listing is ordered, and the last one is the current file
                files.AddRange(backups.OrderByDescending(b => b.When).Select(b => b.File)); // newest backup first
            }
            if (p.IoId is Guid extraIo && !string.IsNullOrWhiteSpace(p.Key) && !listed.Contains(extraIo + "|" + p.Key)) {
                var key = p.Key.SplitKey();
                var io = _server.GetIO(extraIo);
                var name = logFileProviders(c).FirstOrDefault(provider => provider.Id == extraIo).Name ?? "";
                files.Add(describeLogFile(io, extraIo, name, key, io.GetFileSizeOrZeroIfUnknown(key), null,
                    FileKeyUtility.WAL_IsBackUpFileKey(key), extraIo == dbIoId && key.IsSameKey(currentKey), c));
            }
            return (object?)files;
        });
        // The picture the dialog can draw of a log file: every transaction in it, gathered into
        // slices of equal width (see LogFileScan.Timeline). It walks the whole file, which on a big
        // one outlasts a request, so it runs as a job the dialog polls and can give up on - in the
        // registry the file store scans use, so one database cannot be reading two of these at once.
        Commands.Register("db-log-scan-start", ctx => {
            var p = ctx.Payload<LogScanPayload>();
            var c = getContainer(p.StoreId);
            var io = _server.GetIO(p.IoId);
            var key = p.Key.SplitKey();
            if (io.DoesNotExistOrIsEmpty(key)) throw new Exception("The file is empty or does not exist. ");
            if (isLiveLogFile(c, p.IoId, key))
                throw new Exception("The database is open and holds this file to itself. It has to be closed before the file can be read. ");
            var name = key.AsKeyString();
            var job = FileScanJobs.Start(p.StoreId, "log timeline", j => Task.FromResult((object)LogFileScan.Timeline(
                io, key, p.FromPosition, p.ToPosition, p.Slices,
                (done, total) => j.SetProgress("Reading " + name + "…", total > 0 ? (int)(done * 100 / total) : 100),
                j.Cancellation.Token)));
            return (object?)new { JobId = job.Id };
        });
        Commands.Register("db-log-scan-progress", ctx => {
            var p = ctx.Payload<FileScanJobPayload>();
            var job = FileScanJobs.Get(p.JobId);
            // the timeline is only set once the job is done, so the slices travel once rather than
            // on every poll
            return (object?)new { job.State, job.Description, job.Percent, job.Error, Timeline = job.Result as LogFileTimeline };
        });
        Commands.Register("db-log-scan-cancel", ctx => {
            var p = ctx.Payload<FileScanJobPayload>();
            FileScanJobs.Get(p.JobId).Cancellation.Cancel();
            return (object?)new { Cancelled = true };
        });
        // The database as it was at a moment in time: a log file is copied up to the last transaction
        // at or before it and the copy takes over. The log copied from is by default the one the
        // database is running on; IoId/Key name another, which is how an older file or a backup is
        // gone back into rather than restored whole. Nothing is deleted either way - the file that
        // was in place stays one file key behind, and the Files page can make it the database again.
        Commands.Register("db-time-travel", ctx => {
            var p = ctx.Payload<TimeTravelPayload>();
            var c = getContainer(p.StoreId);
            if (c.IsOpenOrOpening()) throw new Exception("The database must be closed first. ");
            var (currentIo, currentKey) = currentLogFile(c);
            var io = currentIo;
            var sourceKey = currentKey;
            var fromCurrent = true;
            if (p.IoId is Guid sourceIoId && !string.IsNullOrWhiteSpace(p.Key)) {
                io = _server.GetIO(sourceIoId);
                sourceKey = p.Key.SplitKey();
                if (io.DoesNotExistOrIsEmpty(sourceKey)) throw new Exception("The file is empty or does not exist. ");
                fromCurrent = sourceIoId == c.Settings.IoDatabase && sourceKey.IsSameKey(currentKey);
            }
            // whatever the client's timezone did to the text, the log is measured in UTC ticks
            var untilUtc = p.UntilUtc.Kind == DateTimeKind.Utc ? p.UntilUtc : p.UntilUtc.ToUniversalTime();
            var cut = LogFileScan.Until(io, sourceKey, untilUtc);
            if (cut.FirstTimestamp > 0 && untilUtc.Ticks < cut.FirstTimestamp)
                throw new Exception(sourceKey.AsKeyString() + " begins at UTC " + cut.FirstUtc!.Value.ToString("u")
                    + ". Copying it up to an earlier moment would leave nothing at all. ");
            // Leaving nothing out is only a mistake when the file being cut is the one in use: the
            // database would be replaced by a copy of itself. From any other file it is the whole of
            // that file becoming the database, which is a thing somebody can mean to do.
            if (cut.TransactionsDropped == 0 && fromCurrent)
                throw new Exception("The database file already ends at UTC " + (cut.LastUtc?.ToString("u") ?? "its beginning")
                    + ", so there is nothing after " + untilUtc.ToString("u") + " to leave out. ");
            var newKey = switchToLogFile(c, (dbIo, destKey) => LogFileScan.Copy(io, sourceKey, dbIo, destKey, cut.KeepEnd, Guid.NewGuid()));
            return (object?)new {
                NewKey = newKey.AsKeyString(),
                SourceKey = sourceKey.AsKeyString(),
                PreviousKey = currentKey.AsKeyString(),
                LastChangeUtc = utc(cut.LastKeptUtc),
                DroppedFromUtc = utc(cut.LastUtc),
                cut.TransactionsKept,
                cut.TransactionsDropped,
                cut.ActionsKept,
                cut.ActionsDropped,
                cut.BytesKept,
                cut.BytesDropped,
            };
        });
        // Makes any file the database: it is copied onto the next WAL file key and everything built
        // from the old log is dropped. The file is only read, so the one the database is running on
        // can be adopted back after a time travel, and a copy someone dropped in the Files page can
        // be tried without moving it anywhere first.
        Commands.Register("db-adopt-file", ctx => {
            var p = ctx.Payload<AdoptFilePayload>();
            var c = getContainer(p.StoreId);
            if (c.IsOpenOrOpening()) throw new Exception("The database must be closed first. ");
            var sourceIo = _server.GetIO(p.IoId);
            var sourceKey = p.Key.SplitKey();
            if (sourceIo.DoesNotExistOrIsEmpty(sourceKey)) throw new Exception("The file is empty or does not exist. ");
            var header = LogFileScan.ReadHeader(sourceIo, sourceKey); // refuses anything that is not a log file
            var size = sourceIo.GetFileSizeOrZeroIfUnknown(sourceKey);
            var newKey = switchToLogFile(c, (dbIo, destKey) => LogFileScan.Copy(sourceIo, sourceKey, dbIo, destKey, size, Guid.NewGuid()));
            return (object?)new { NewKey = newKey.AsKeyString(), Size = size, FirstChangeUtc = utc(header.FirstUtc) };
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
        // the database is a single WAL file; downloading it is a copy of the database, and every
        // way of putting one in place (uploading, restoring, going back in time) writes the next
        // file key and reopens on it
        Commands.Register("db-file-info", ctx => {
            var p = ctx.Payload<IoListPayload>();
            var c = getContainer(p.StoreId);
            var ioId = c.Settings.IoDatabase ?? throw new Exception("No database IO provider configured. ");
            var io = _server.GetIO(ioId);
            var current = FileKeyUtility.WAL_GetLatestFileKey(io);
            // The storage type decides when an upload can close the database: the files of a memory
            // provider live in the instance the server drops when the last database closes, so a
            // file staged there has to be put in place while that instance is still the one in use.
            var ioType = (c.Settings.IOSettings ?? []).FirstOrDefault(s => s.Id == ioId)?.IOType ?? IOTypes.LocalDisk;
            return (object?)new {
                IoId = ioId,
                CurrentKey = current.AsKeyString(),
                Size = io.GetFileSizeOrZeroIfUnknown(current),
                State = c.HasFailed ? "Error" : c.Store?.State.ToString() ?? "Closed",
                IoType = ioType.ToString(),
            };
        });
        // The end of a database upload: the staged file becomes the database. The upload itself runs
        // against an open database - it only writes a temp file in the upload folder - and the client
        // closes the database once the last byte is in, so the downtime is the swap rather than the
        // transfer. Which key the file lands on is decided here, once the database is closed and the
        // file has arrived, rather than named by the client before the upload started - by then the
        // log may have moved on, and the next key from back then can be the live file.
        Commands.Register("db-upload-adopt", ctx => {
            var p = ctx.Payload<UploadAdoptPayload>();
            var c = getContainer(p.StoreId);
            if (c.IsOpenOrOpening()) throw new Exception("The database must be closed before the uploaded file can be put in place. ");
            var io = _server.GetIO(p.IoId);
            var temp = UIFileTransfer.UploadTempKey(p.UploadId);
            var received = io.GetFileSizeOrZeroIfUnknown(temp);
            if (received == 0) throw new Exception("The upload is missing; nothing was staged. ");
            if (received != p.Size) throw new Exception($"The upload holds {received} bytes, {p.Size} were expected. ");
            LogFileHeader header;
            try {
                header = LogFileScan.ReadHeader(io, temp); // refuses anything that is not a database file
            } catch {
                io.DeleteFileIfItExists(temp);
                throw;
            }
            // The index engines rebuild when the log file id is one they have not seen, which is
            // what an uploaded database normally is - so the upload is moved into place as it is.
            // A file carrying the same id as the log being replaced (re-uploading a download of it)
            // would look to them like the log they are already up to date with, so that one is
            // copied with an id of its own instead.
            var sameId = header.FileId == currentLogFileId(c);
            var newKey = switchToLogFile(c, (dbIo, destKey) => {
                var canMove = !sameId && p.IoId == c.Settings.IoDatabase && io.CanRenameFile;
                if (canMove) io.RenameFile(temp, destKey);
                else LogFileScan.Copy(io, temp, dbIo, destKey, received, sameId ? Guid.NewGuid() : null);
            });
            io.DeleteFileIfItExists(temp); // a no-op when it was renamed into place
            return (object?)new { NewKey = newKey.AsKeyString(), Size = received, FirstChangeUtc = utc(header.FirstUtc) };
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
        // Both "collect garbage" buttons in the UI - the one on the server overview and the one on a
        // database dashboard - come here: there is one heap behind every database on the server, so
        // there is nothing per-database to do. See MemoryReclaim for what makes the collection deep.
        Commands.Register("collect-garbage", ctx => {
            static string mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("N1") + " MB";
            var r = MemoryReclaim.Collect();
            return (object?)new {
                Started = true,
                // resident is what the operating system got back, and the point of a compacting
                // aggressive collection: the managed heap can fall while the process shrinks by
                // nothing. Committed has no before to compare against, see MemoryReclaimResult.
                Message = $"{r.Collections} collections in {r.ElapsedMs:N0} ms. "
                    + $"Managed {mb(r.ManagedBefore)} → {mb(r.ManagedAfter)}, "
                    + $"resident {mb(r.WorkingSetBefore)} → {mb(r.WorkingSetAfter)}, "
                    + $"{mb(r.Committed)} still committed.",
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
/// <summary>UntilUtc is the moment the copy of the log should end at, as UTC. IoId and Key name the
/// log file to copy; without them it is the one the database is running on.</summary>
sealed record TimeTravelPayload(Guid StoreId, DateTime UntilUtc, Guid? IoId = null, string? Key = null);
/// <summary>IoId and Key name one more file to list beside the ones found in the data and backup
/// folders - the file the dialog was opened from, wherever it happens to lie.</summary>
sealed record LogFilesPayload(Guid StoreId, Guid? IoId = null, string? Key = null);
/// <summary>The log file to draw, and how much of it: FromPosition and ToPosition are transaction
/// boundaries from a previous scan's slices (0 for the whole file), Slices how many the picture is
/// gathered into.</summary>
sealed record LogScanPayload(Guid StoreId, Guid IoId, string Key, long FromPosition = 0, long ToPosition = 0, int Slices = 1200);
/// <summary>The file to make the database, in any of the server's storages.</summary>
sealed record AdoptFilePayload(Guid StoreId, Guid IoId, string Key);
/// <summary>A database upload staged through ui/upload-part, ready to take over.</summary>
sealed record UploadAdoptPayload(Guid StoreId, Guid IoId, Guid UploadId, long Size);
sealed record TruncatePayload(Guid StoreId, bool KeepOld);
sealed record IoFolderPayload(Guid IoId, string? Path, bool Recursive = false);
/// <summary>Key is the file (rename file), the folder (rename folder) or the parent folder (create folder).</summary>
sealed record IoRenamePayload(Guid IoId, string Key, string NewName);
sealed class IoInfo {
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    /// <summary>"storage" for a provider from the database settings, "projectRoot" for the website project folder.</summary>
    public string Kind { get; init; } = "storage";
    public bool CanRenameFile { get; init; }
    public bool CanRenameFolder { get; init; }
    public bool SupportsEmptyFolders { get; init; }
    /// <summary>The folder on the server, only for the project root: the UI names it in its notice.</summary>
    public string? LocalPath { get; init; }
}
sealed record ZipRequestPayload(Guid IoId, string[] Keys, string? BasePath);
sealed record IoDeleteFilesPayload(Guid IoId, string[] Keys);
sealed record FileScanStartPayload(Guid StoreId, string Scan, bool CountOnly);
sealed record FileScanJobPayload(Guid JobId);
sealed record ConversionCancelPayload(Guid StoreId, Guid Id, bool Permanently);
