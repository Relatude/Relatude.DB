# Files, media URLs and uploads

Everything between "a node has a `FileValue` property" and "a browser renders a cropped WebP of it". Four moving parts:

1. **Storage** — the bytes live in a file store (local disk, Azure blob), never in the node.
2. **URLs** — `GetUrl` builds a URL that encodes *which file* and *which variant of it* you want.
3. **A middleware** — you write it; it turns those URLs back into an HTTP response.
4. **Conversion** — variants are produced asynchronously by registered converters.

## Contents

- [Namespaces](#namespaces)
- [The file property](#the-file-property)
- [Uploading a whole file](#uploading-a-whole-file)
- [Partial (multipart) uploads](#partial-multipart-uploads)
- [Generating URLs](#generating-urls)
- [FileAdjustment: asking for a variant](#fileadjustment-asking-for-a-variant)
- [Conversion is asynchronous](#conversion-is-asynchronous)
- [The download middleware](#the-download-middleware)
- [What FileHandler does for you](#what-filehandler-does-for-you)
- [Pitfalls](#pitfalls)
- [Where to look in the source](#where-to-look-in-the-source)

## Namespaces

```csharp
using Relatude.DB.Common;          // FileValue, FileFormat, FileType, PropertyPath
using Relatude.DB.FileConversion;  // FileAdjustment, FileAdjustmentImage/Video/Meta, ImageCropMode
using Relatude.DB.Web;             // UrlContent, UrlKeys, UrlTarget, FileHandler
using Relatude.DB.NodeServer;      // RelatudeDBContext, RelatudeDBRuntime
```

## The file property

```csharp
[FileProperty]
public FileValue Photo { get; set; } = FileValue.Empty;

// pin a property to a specific storage provider (the id comes from the admin UI)
[FileProperty(FileStorageProviderId = "b1c2d3e4-…")]
public FileValue Brochure { get; set; } = FileValue.Empty;
```

`FileValue` is a value you read, not one you build — uploading writes it onto the node for you. What it carries:

| Member | What it is |
|---|---|
| `IsEmpty` | No file in the slot. Check this before doing anything else. |
| `Name` | The stored file name, extension included. |
| `Size` | Bytes. |
| `Hash` | Content hash. Also the file key in the store, and the input to URL cache-busting. |
| `Width` / `Height` | Pixel dimensions for images and video, filled in by metadata extraction after upload. |
| `FileType` | `Image`, `Video`, `Audio`, `Document`, `Meta`, `Unknown` — derived from `Name`. |
| `Format` | The detailed `FileFormat` (`Jpeg`, `Png`, `Webp`, `Mp4`, `Pdf`, …), also derived from `Name`. |
| `ContentType` | The MIME type for `Format`. |
| `TextExtract` | Extracted text, which is what makes document contents searchable. |
| `MetaJSON` | Extracted metadata as JSON. |
| `PropertyPath` | Which node and property this file hangs off. **`null` until the node is stored.** |
| `FileId` / `StorageId` | The file's own id, and the id of the store holding it. |

`FileType` and `Format` come from the *file name*, not from sniffing the bytes. A file uploaded with the wrong extension is classified wrong, and conversion will then look for a converter that does not fit.

## Uploading a whole file

The node must exist in the store first — `FileValue.PropertyPath` is what addresses the upload, and it is `null` on an unsaved node.

```csharp
var article = db.CreateAndInsert<IArticle>(a => a.Title = "Winter session");
article = db.Get(article);                       // re-read so Photo.PropertyPath is set

// by expression — the readable form
await db.FileUploadAsync<IArticle>(article, a => a.Photo, @"C:\media\cover.jpg");
await db.FileUploadAsync<IArticle>(article, a => a.Photo, stream, "cover.jpg");
await db.FileUploadAsync<IArticle>(article, a => a.Photo, bytes,  "cover.jpg");

// when you already hold the FileValue slot
await db.FileUploadAsync(article.Photo, @"C:\media\cover.jpg");

// from another IO provider (bulk import, migration)
await db.FileUploadAsync<IArticle>(article, a => a.Photo, ioProvider, sourceFileKey, "cover.jpg");
```

The upload writes the bytes **and** updates the `FileValue` on the node, so you do not `Update` the node afterwards. Reading back:

```csharp
await db.FileDownloadAsync(article.Photo, @"C:\tmp\cover.jpg");
byte[] bytes  = await db.FileDownloadAsync<IArticle>(article, a => a.Photo);
Stream stream = await db.OpenFileDownloadStreamAsync(article.Photo);   // starts before the transfer finishes
await db.FileDeleteAsync<IArticle>(article, a => a.Photo);

bool there = await db.FileUploadedAndAvailableAsync<IArticle>(article, a => a.Photo);
```

`FileUploadedAndAvailableAsync` matters for remote stores, where the write can complete in the background after the call returns.

## Partial (multipart) uploads

For files too large to push through one request — video especially, and anything that would hit ASP.NET's request body size limit. The upload is chunked into `Initiate` → `Append`* → `Finalize`.

```csharp
if (!db.FileStoreSupportsMultipartUploads(article.Photo))
    throw new Exception("This file store has no multipart support.");

var uploadId = await db.InitiateMultipartUploadAsync(article.Photo, "walkthrough.mp4");
foreach (var chunk in chunks) {
    await db.AppendMultipartUploadAsync(uploadId, chunk, chunk.Length);   // in order
}
FileValue value = await db.FinalizeMultipartUploadAsync(uploadId);

// on failure
await db.CancelMultipartUploadAsync(uploadId);
```

`InitiateMultipartUploadAsync` also takes a `PropertyPath` instead of a `FileValue`. `FinalizeMultipartUploadAsync` takes an optional `maxWaitForMetaUpdate` in ms — how long to wait for metadata extraction (dimensions, text extract) before returning.

### The rules that are not obvious

- **Chunks must be appended in order**, and one at a time per upload id. The store appends to an open file and folds each chunk into a running hash — parallel or out-of-order appends corrupt both. Parallelism belongs *between* files, not within one.
- **The session lives in memory on the store instance.** A load-balanced deployment needs the whole upload pinned to one instance, or it fails with "Upload session not found".
- **Sessions expire after 10 minutes of inactivity** and the partial file is deleted. The clock resets on every append.
- **Not every file store supports it.** Only stores implementing `IFileStoreMultiPartSupport` do — `MultiFileStore` today. `FileStoreSupportsMultipartUploads` is the check; it throws "File store does not support multipart upload" if you skip it.
- **The node must already be stored**, exactly as for whole-file uploads.
- The `FileValue` — including the content hash — is only written onto the node at **finalize**. A cancelled or expired upload leaves the node untouched.
- Transaction plugins get their `OnAfterFileUpload` callback at finalize too.

### Server endpoints

Three endpoints are enough. Keep the chunk in memory only for the length of the request:

```csharp
app.MapPost("/upload/start", async (RelatudeDBContext ctx, string fileName) => {
    var db = ctx.Database;
    var article = db.CreateAndInsert<IArticle>(a => a.Title = fileName);
    var uploadId = await db.InitiateMultipartUploadAsync(article.Photo, fileName);
    return Results.Json(uploadId);
});

app.MapPost("/upload/part", async (RelatudeDBContext ctx, Guid uploadId, HttpRequest req) => {
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var part = ms.ToArray();
    await ctx.Database.AppendMultipartUploadAsync(uploadId, part, part.Length);
});

app.MapPost("/upload/complete", async (RelatudeDBContext ctx, Guid uploadId) => {
    await ctx.Database.FinalizeMultipartUploadAsync(uploadId);
});
```

`article.Photo` is readable straight off the node returned by `CreateAndInsert`, because that node is already stored.

### Browser side

```js
const CHUNK_SIZE = 2 * 1024 * 1024;   // 2 MB

async function uploadFile(file) {
    const startRes = await fetch(`/upload/start?fileName=${encodeURIComponent(file.name)}`, { method: 'POST' });
    if (!startRes.ok) throw new Error(`start failed: ${startRes.status}`);
    const uploadId = await startRes.json();

    const total = Math.ceil(file.size / CHUNK_SIZE);
    for (let i = 0; i < total; i++) {                       // sequential — required
        const chunk = file.slice(i * CHUNK_SIZE, (i + 1) * CHUNK_SIZE);
        const res = await fetch(`/upload/part?uploadId=${encodeURIComponent(uploadId)}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/octet-stream' },
            body: chunk
        });
        if (!res.ok) throw new Error(`part ${i} failed: ${res.status}`);
        onProgress((i + 1) / total);
    }

    const done = await fetch(`/upload/complete?uploadId=${encodeURIComponent(uploadId)}`, { method: 'POST' });
    if (!done.ok) throw new Error(`complete failed: ${done.status}`);
}
```

Several *files* may upload concurrently; the chunk loop within one file may not. A complete drag-and-drop implementation with progress bars is in `examples/Website.Simple/wwwroot/upload.html`.

## Generating URLs

```csharp
db.GetUrl(article);                       // the node's own URL
db.GetUrl(article.Photo);                 // the file, as uploaded
db.GetUrl(article.Photo, adjustment);     // a variant of the file
db.GetUrl(propertyPath, adjustment);      // same, addressed by property path
```

The same overloads exist on `db.Datastore` (`db.Datastore.GetUrl(article.Photo.PropertyPath!, adj)`), which is what you use when you hold a `PropertyPath` rather than a node.

A generated file URL looks roughly like:

```
/some-node-address/cover.webp?nid=a<base64url payload>
```

- The **path** is the node's address (or its id, depending on the URL format), with the file name appended and the extension swapped for the one implied by `RequestedFormat`.
- The **`nid` query parameter** carries a type character (`k` node, `n` embedded node, `p` file property, `a` adjusted file property) followed by a Brotli-compressed, base64url-encoded payload holding the property path, the serialised adjustment, and a content version id.
- The **content version id** is derived from the file's content hash plus a per-process Guid, which is what busts caches when a file is replaced.

Two consequences worth internalising:

- **The variant is defined entirely by the URL.** There is no server-side registry of "allowed sizes" — the middleware decodes whatever adjustment the URL carries and converts on demand.
- **`GetUrl` requires the property to actually hold a file.** It reads the `FileValue` to build the version id and file name, and throws `Property at path … does not contain a file` when the slot is empty. Guard with `if (!file.IsEmpty)`.

### URL options

`UrlProviderOptions` (in `Relatude.DB.Web`, `Relatude.DB.DataStoreLocal`) shapes all of this:

| Option | Effect |
|---|---|
| `UrlFormat` | `AddressOrGuidIdAsPath` (default), `AddressOrIntIdAsPath`, `AddressAndIntIdAsPath`, `AddressAndGuidIdAsPath`, `IntIdOnlyAsPath`, `GuidIdOnlyAsPath`, `IntIdAsQuery`, `GuidIdAsQuery`. |
| `UrlNodeRoot` | Prefix all node/file URLs, e.g. `"assets"`. Defaults to `/` — meaning file URLs live at the site root. |
| `UrlParamName` | The query parameter name. Default `nid`. |
| `IncludeTrailingSlash` | Trailing slash on node URLs. |
| `HashPropertyUrls` + `HashKey` | HMAC-SHA256 over the payload, verified on parse. Tampered or hand-crafted URLs are rejected. |
| `UnCompressed` | Skip Brotli compression of the payload. Longer URLs, less CPU. |

**Two honest caveats about the current build:**

- **`absolute: true` is not implemented.** `DefaultUrlProvider` throws `NotImplementedException("Absolute URLs are not implemented yet.")`. Build absolute URLs by prefixing the relative one with your own scheme+host.
- **These options are not exposed through `ServerOptions`.** `NodeStoreContainer` constructs `DefaultUrlProvider` itself and sets only `HashKey` (to the store settings id); everything else is at its default. To change them today you construct `DataStoreLocal` yourself and pass your own `IUrlProvider`. Say so rather than inventing a configuration hook.

### Parsing URLs back

```csharp
db.TryParseUrl(url, out UrlKeys keys);                        // what does this URL point at?
db.TryParseUrlForContent(url, out UrlContent content, maxWaitMs);  // …and resolve it to servable content
```

`UrlKeys` gives you `Target` (`Node` / `EmbeddedNode` / `Property` / `PropertyAdjusted`), `NodeKey`, `NodePath`, `PropertyPath` and `Adjustment`. `UrlContent` adds the resolved payload: `NodeData` for node targets, and `Stream`, `FileName`, `ContentType`, `FileValue`, `Cacheable` for file targets.

Both return `false` — they do not throw — when the URL is not one of ours, which is what lets the middleware fall through cleanly.

## FileAdjustment: asking for a variant

An adjustment is a plain object describing the output you want. Three kinds:

### FileAdjustmentImage

```csharp
var thumb = new FileAdjustmentImage {
    Width = 440,
    Height = 400,
    CropMode = ImageCropMode.Fill,
    RequestedFormat = FileFormat.Webp,
    Quality = 85,
};
```

| Property | Range / values | Notes |
|---|---|---|
| `Width`, `Height` | 1–10 000 | Canvas size. |
| `CropMode` | `Fill`, `Fit`, `Stretch`, `Auto` | `Fill` crops to fill, `Fit` letterboxes, `Stretch` distorts, `Auto` picks between Fill and Fit from image content. |
| `Zoom` | 0.1–10 000 | Percent. 100 = 1:1, 200 = 2× in. |
| `FocusX`, `FocusY` | ±10 000 | Focal point, in original-image coordinates. |
| `OffsetX`, `OffsetY` | ±10 000 | Pan, in original-image coordinates. |
| `Rotation` | ±360 | Degrees. |
| `Brightness`, `Contrast`, `Saturation` | ±100 | 0 = unchanged. |
| `HueShift` | ±180 | Degrees. |
| `Sharpness` | ±100 | 0 = unchanged. |
| `InvertLuminance` | bool | Inverts all colours and shifts the hue 180° back, so lightness flips but hues survive. Applied before the adjustments above. |
| `AutoLightDarkMode` | `None`, `AdaptToLightModeIfNeeded`, `AdaptToDarkModeIfNeeded` | Applies `InvertLuminance` only if the image looks made for the opposite surface. Photographs are never inverted. |
| `Quality` | 0–100 | Lossy formats only. |
| `BackgroundColor` | `"#RRGGBB"` / `"#RRGGBBAA"` | Used when the canvas is larger than the resized image. |
| `AutoBackgroundColor` | bool | Pick the background from edge analysis instead. |
| `RequestedFormat` | `Jpeg`, `Png`, `Webp`, `Avif`, `Gif`, `Bmp` | Falls back to `Png` when left `Unknown`. |
| `TimeOffsetMs` | ms | **Video sources**: which frame to grab as a still. |
| `TimeOffsetPercentage` | 0–100 | Same, as a percentage of duration. |
| `Temporary` | bool | See below. |

Out-of-range values are clamped rather than rejected, and a `Width`/`Height`/`Zoom` of zero or less becomes "unset".

An image adjustment applied to a **video** file extracts a still — that is what `TimeOffsetMs` is for, and it is the standard way to show a poster frame while the video transcode is still running.

### FileAdjustmentVideo

```csharp
var video = new FileAdjustmentVideo {
    Width = 640,
    Height = 360,
    TargetBitRateInMbps = 2.5,     // clamped to 0.01–100
    CropNotZoom = false,           // true crops to the target aspect instead of zooming
    RequestedFormat = FileFormat.Mp4,
};
```

### FileAdjustmentMeta

```csharp
var metaUrl = db.GetUrl(article.Photo, new FileAdjustmentMeta());
```

Returns `FileMetaJson` — conversion status and extracted metadata as JSON. It sets `Temporary = true` in its constructor, so it is not persisted to the disk cache. Useful for a progress endpoint a client can poll.

### The adjustment *is* the cache key

Every adjustment hashes all of its fields into a key; the conversion cache is keyed on `(file id, that key)`. Identical parameters hit the cache, and any difference — one pixel of width, a different quality — is a new conversion and a new cached file.

So: **define a small set of named presets and reuse them.** Generating adjustments from arbitrary request parameters means an unbounded number of conversions and cache entries, and since anyone can craft a URL, that is a denial-of-service surface. `HashPropertyUrls = true` closes it by rejecting URLs your own code did not sign.

`Temporary = true` keeps a small conversion result in memory rather than persisting it to the disk cache — right for one-off or rapidly-changing variants, wrong for anything served repeatedly.

## Conversion is asynchronous

Converters are registered at startup and there is no built-in default — without them every conversion fails with `No converter available from JPEG to WEBP`:

```csharp
builder.AddRelatudeDB(options => {
    options.FileConverters.Add(new SkiaImageConverter(1));   // images
    options.FileConverters.Add(new FFMpegVideoConverter());  // video
});
```

Asking for a URL does not produce the file. The conversion is queued and runs in the background:

```csharp
var path = article.Photo.PropertyPath!;

bool ready = db.IsFileReady(path, adj, requestIfNot: true);  // queues it if not started
db.EnsureConversionRequested(path, adj);                     // queue and return immediately

if (db.TryGetConversionInfo(path, adj, queueConversionIfNotRequested: true, out var progress)) {
    // progress.Status: InProgress / Ready / Error, plus progress numbers
}

FileConversions running = db.GetRunningConversions();
await db.Datastore.CancelConversion(conversionKey, permanently: false);
```

**When a variant is not ready, the request does not fail.** The store returns a generated placeholder in the requested format — a yellow "in progress" frame, a red frame carrying the error text — and reports `IsReady = false`. That is why `UrlContent.Cacheable` exists and why the middleware must honour it: caching a placeholder for 30 days would be a bug that outlives the conversion by a month.

`maxWaitMs` (on `TryParseUrlForContent`, `GetFileStream`, `GetFileStreamAndState`) is how long to block hoping the conversion finishes; `-1` means the store's own default.

The pattern for video, straight from `examples/Website.Simple`: check `IsFileReady` for the transcode, serve the `<video>` when it is ready, and fall back to an image adjustment with a `TimeOffsetMs` poster frame while it is not.

```csharp
if (db.Datastore.IsFileReady(path, videoAdj, true)) {
    var videoUrl = db.Datastore.GetUrl(path, videoAdj);
    // <video><source src="@videoUrl" type="video/mp4"></video>
} else {
    var posterUrl = db.Datastore.GetUrl(path, posterAdj);   // FileAdjustmentImage + TimeOffsetMs
    // <img src="@posterUrl">
}
```

## The download middleware

Relatude.DB does **not** map a file endpoint for you. `MapRelatudeDBClient()` is currently a no-op, and the admin UI's own routes are separate. Serving media is a middleware you write — it is about 30 lines, and this is the whole of it:

```csharp
using Relatude.DB.NodeServer;
using Relatude.DB.Web;

public class RelatudeDBMiddleware(RequestDelegate next) {

    public async Task Invoke(HttpContext http, RelatudeDBContext ctx) {
        if (RelatudeDBRuntime.IsReady) {                       // store may still be opening
            var url = http.Request.Path.Value + http.Request.QueryString;
            if (ctx.Database.TryParseUrlForContent(url, out var content)) {
                var result = await handleRequest(http, content);
                if (result != null) {
                    await result.ExecuteAsync(http);
                    return;
                }
            }
        }
        await next.Invoke(http);                               // not ours — pass it on
    }

    async Task<IResult?> handleRequest(HttpContext http, UrlContent content) => content.Id.Target switch {
        UrlTarget.Property or UrlTarget.PropertyAdjusted => await handleFile(http, content),
        UrlTarget.Node or UrlTarget.EmbeddedNode         => await handlePage(http, content),
        _ => null,
    };

    async Task<IResult?> handleFile(HttpContext http, UrlContent c)
        => await FileHandler.HandleFileAsync(http, c.Stream, c.FileName, c.Attachment, c.ContentType, c.Cacheable);

    Task<IResult?> handlePage(HttpContext http, UrlContent c)
        => Task.FromResult<IResult?>(Results.Json(c.NodeData));   // render your own page here
}
```

Registered in `Program.cs`:

```csharp
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseMiddleware<RelatudeDBMiddleware>();

app.StartRelatudeDB();
app.MapRelatudeDBAdmin();
```

Four things to get right:

1. **The `RelatudeDBRuntime.IsReady` gate.** The store opens asynchronously; without the gate, requests arriving during startup throw instead of falling through.
2. **Pass `Path + QueryString`, not `Path`.** The whole addressing payload is in the query parameter — parsing the path alone silently fails to match anything.
3. **Order.** The default `UrlNodeRoot` is `/`, so this middleware sees *every* request. Put it after `UseStaticFiles()` so real static files win, and make sure every non-match calls `next` — which the `TryParse…` shape does naturally, since it returns `false` rather than throwing.
4. **Node targets are yours to render.** `UrlTarget.Node` / `EmbeddedNode` hand you `UrlContent.NodeData`; returning JSON is a placeholder, not a design. Return your own view, or `null` to fall through to MVC/Razor routing.

### Access control

This middleware serves whatever the URL points at. Filtering by the caller's permissions is done by passing a `QueryContext` — the `TryParseUrlForContent` overload takes one, and the default is the store's own context. Never hand a request handler `db.Context.Admin()`.

## What FileHandler does for you

`FileHandler.HandleFileAsync(http, stream, fileName, attachment, contentType, cached)` in `Relatude.DB.Web` is the small helper the middleware delegates to. It handles:

- **Caching.** `cached: true` → `Cache-Control: public, max-age=30 days`. `cached: false` → `no-cache`. `null` → no header at all. Pass `UrlContent.Cacheable` straight through, so in-progress placeholders are never cached.
- **Content-Disposition.** `inline` by default, `attachment` when you pass `attachment: true`, with both an ASCII-stripped `filename` and a UTF-8 `filename*` so non-ASCII names survive old and new browsers.
- **Range requests.** A `Range: bytes=…` header on a seekable stream produces a `206 Partial Content` with the right `Content-Range`, and `Accept-Ranges: bytes` is advertised otherwise. **This is what makes video seeking work** — without it a browser can only play from the start.
- **Stream lifetime.** The range path disposes the stream itself; the normal path hands it to `Results.Stream`, which disposes it after the response completes. Do not wrap the call in a `using`.

## Pitfalls

- **Upload after insert.** `FileValue.PropertyPath` is `null` on an unsaved node, and both upload paths throw on it.
- **`GetUrl` throws on an empty file slot.** Always guard with `if (!file.IsEmpty)`.
- **`absolute: true` throws** in the current `DefaultUrlProvider`.
- **A URL you just generated is usually not ready yet.** Conversion is queued, not immediate. Check `IsFileReady`, or accept that the first request serves a status placeholder.
- **Never cache a response whose `Cacheable` is `false`** — that is a placeholder, not the file.
- **The adjustment is the cache key.** Vary it per request and you get an unbounded number of conversions. Use presets; turn on `HashPropertyUrls` in production.
- **`FileType` / `Format` come from the file name**, so a wrong extension routes the file to the wrong converter.
- **Multipart chunks are strictly ordered**, sessions are in-memory and per-instance, and they expire after 10 minutes idle.
- **Only stores implementing `IFileStoreMultiPartSupport` accept chunked uploads.** Check `FileStoreSupportsMultipartUploads` first.
- **Register file converters at startup**, or every conversion returns "No converter available".
- **Put the media middleware after `UseStaticFiles()`** and always fall through when the URL is not a Relatude URL.

## Where to look in the source

| Topic | Source path |
|---|---|
| `FileValue` | `src/Relatude.DB.Common/Common/FileValue.cs` |
| `FileFormat` / `FileType` / MIME mapping | `src/Relatude.DB.Common/Common/FileFormats.cs` |
| Adjustment types and their serialisation | `src/Relatude.DB.DataStore/FileConversion/FileAdjustment.cs` |
| `ImageCropMode`, image metadata | `src/Relatude.DB.DataStore/FileConversion/ImageMeta.cs` |
| Conversion queue, status placeholders | `src/Relatude.DB.DataStore/FileConversion/FileConversionEngine.cs`, `FileConversionCache.cs` |
| URL building and parsing | `src/Relatude.DB.DataStoreLocal/Web/DefaultUrlProvider.cs`, `src/Relatude.DB.DataStore/Web/IUrlProvider.cs` |
| `UrlContent` / `UrlKeys` | `src/Relatude.DB.DataStore/Web/UrlKeys.cs` |
| URL → content resolution | `src/Relatude.DB.DataStoreLocal/DataStores/DataStoreLocal.Urls.cs` |
| Upload / download / multipart on the store | `src/Relatude.DB.DataStoreLocal/DataStores/DataStoreLocal.Files.cs`, `DataStores/Uploads/UploadSessions.cs` |
| File store contracts | `src/Relatude.DB.FileStorage/DataStores/Files/IFileStore.cs`, `MultiFileStore.cs` |
| HTTP response helper | `src/Relatude.DB.NodeServer/Web/FileHandler.cs` |
| A complete working example | `examples/Website.Simple/` — `Program.cs`, `MiddelWare.cs`, `wwwroot/upload.html` |
