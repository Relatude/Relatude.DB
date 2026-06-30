using Microsoft.Net.Http.Headers;
using Relatude.DB.Common;
using Relatude.DB.FileConversion;
using Relatude.DB.IO;
using Relatude.DB.NodeServer;

namespace Relatude.DB.Web;

public static class FileHandler {
    public static async Task<IResult> HandleFileAsync(RelatudeDBContext ctx, HttpContext http, PropertyPath path) {
        var db = ctx.Database;
        var info = await db.GetFileStreamAndValue(path);
        return await HandleFileAsync(ctx, http, info.Stream, info.FileValue.Name, info.FileValue.Format, true);
    }
    public static async Task<IResult> HandleFileAsync(RelatudeDBContext ctx, HttpContext http, PropertyPath path, FileAdjustment adj) {
        var db = ctx.Database;
        var info = await db.GetFileStreamAndState(path, adj);
        var fileName = Path.GetFileNameWithoutExtension(info.FileValue.Name) + FileFormatUtil.GetExtensionWithDot(info.RequestedFormat);
        return await HandleFileAsync(ctx, http, info.Stream, fileName, info.RequestedFormat, info.IsReady);
    }
    public static async Task<IResult> HandleFileAsync(RelatudeDBContext ctx, HttpContext http, Stream stream, string fileName, FileFormat format, bool cached) {
        var totalLength = stream.CanSeek ? stream.Length : (long?)null;
        var rangeHeader = http.Request.Headers.Range.ToString();
        var attachment = FileFormatUtil.AsAttachement(format);
        var contentType = FileFormatUtil.GetContentType(format);
        var dispositionType = attachment ? "attachment" : "inline";
        if (cached) {
            http.Response.GetTypedHeaders().CacheControl = new() { Public = true, MaxAge = TimeSpan.FromDays(30) };
        } else {
            http.Response.GetTypedHeaders().CacheControl = new() { NoCache = true };
        }
        http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue(dispositionType) {
            FileName = new string([.. fileName.Where(c => c <= 127)]), // fallback for non-ASCII file names, older browsers
            FileNameStar = fileName // UTF-8 file name for modern browsers, will be ignored by older browsers
        }.ToString();
        if (stream.CanSeek && !string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=")) {
            try {
                var range = rangeHeader["bytes=".Length..].Split('-');
                var start = long.TryParse(range[0], out var s) ? s : 0;
                var end = range.Length > 1 && long.TryParse(range[1], out var e) ? e : totalLength!.Value - 1;
                end = Math.Min(end, totalLength!.Value - 1);
                var length = end - start + 1;
                if (stream is ReadStreamWrapper wrapper) {
                    if (wrapper.InnerStream is StoreStreamDiscRead storeStream) {
                        // optimization, switch to direct file stream to avoid double buffering
                        var filePath = storeStream.InnerFilePath;
                        stream.Dispose();
                        stream = File.OpenRead(filePath);
                    }
                }
                stream.Seek(start, SeekOrigin.Begin);
                http.Response.StatusCode = 206;
                http.Response.Headers.ContentRange = $"bytes {start}-{end}/{totalLength}";
                http.Response.Headers.AcceptRanges = "bytes";
                http.Response.ContentLength = length;
                http.Response.ContentType = contentType;
                await stream.CopyToAsync(http.Response.Body, (int)Math.Min(length, 81920), http.RequestAborted);
            } finally {
                stream.Dispose(); // ensure stream is disposed after response is completed
            }
            return Results.Empty;
        }
        if (totalLength.HasValue) http.Response.Headers.AcceptRanges = "bytes";
        return Results.Stream(stream, contentType); // stream is disposed by framework after response is completed
    }
}