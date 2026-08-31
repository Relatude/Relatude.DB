using Microsoft.Net.Http.Headers;

namespace Relatude.DB.Web;

public static class FileHandler {
    //public static Task<IResult> HandleFileAsync(HttpContext http, UrlContent c) {
    //    return HandleFileAsync(http, c.Stream, c.FileName, c.Attachment, c.ContentType, c.Cacheable);
    //}
    public static async Task<IResult> HandleFileAsync(HttpContext http, Stream? stream, string? fileName = null, bool? attachment = null, string? contentType = null, bool? cached = null) {
        var rangeHeader = http.Request.Headers.Range.ToString();
        if (!cached.HasValue) {
        } else if (cached.Value) {
            http.Response.GetTypedHeaders().CacheControl = new() { Public = true, MaxAge = TimeSpan.FromDays(30) };
        } else {
            http.Response.GetTypedHeaders().CacheControl = new() { NoCache = true };
        }
        if (fileName != null) {
            if (!attachment.HasValue) attachment = false;
            var dispositionType = attachment.HasValue && attachment.Value ? "attachment" : "inline";
            http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue(dispositionType) {
                FileName = new string([.. fileName.Where(c => c <= 127)]), // fallback for non-ASCII file names, older browsers
                FileNameStar = fileName // UTF-8 file name for modern browsers, will be ignored by older browsers        
            }.ToString();
        }
        if (stream == null) return Results.Empty;
        var totalLength = stream.CanSeek ? stream.Length : (long?)null;
        bool isRangeRequest = !string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=");
        if (stream.CanSeek && isRangeRequest) {
            try {
                // One range, which is what a browser asks for: "bytes=start-end", "bytes=start-" for
                // everything from an offset, and "bytes=-n" for the last n bytes.
                var range = rangeHeader["bytes=".Length..].Split('-');
                long start = 0, end = totalLength!.Value - 1;
                var hasStart = range.Length > 0 && long.TryParse(range[0], out start);
                var hasEnd = range.Length > 1 && long.TryParse(range[1], out end);
                if (!hasStart) start = hasEnd ? Math.Max(0, totalLength.Value - end) : 0; // a suffix range
                if (!hasStart || !hasEnd) end = totalLength.Value - 1;
                end = Math.Min(end, totalLength.Value - 1);
                if (start < 0 || start > end) { // asked for a range the file does not have
                    http.Response.StatusCode = 416;
                    http.Response.Headers.ContentRange = "bytes */" + totalLength.Value;
                    return Results.Empty;
                }
                var length = end - start + 1;
                stream.Seek(start, SeekOrigin.Begin);
                http.Response.StatusCode = 206;
                http.Response.Headers.ContentRange = $"bytes {start}-{end}/{totalLength}";
                http.Response.Headers.AcceptRanges = "bytes";
                http.Response.ContentLength = length;
                if (contentType != null) http.Response.ContentType = contentType;
                await copyRangeAsync(stream, http.Response.Body, length, http.RequestAborted);
            } finally {
                stream.Dispose(); // ensure stream is disposed after response is completed
            }
            return Results.Empty;
        }
        if (totalLength.HasValue) http.Response.Headers.AcceptRanges = "bytes";
        return Results.Stream(stream, contentType); // stream is disposed by framework after response is completed
    }
    // Exactly the bytes of the range and no more: the stream holds the whole file, while the response
    // has already declared the length of the part of it that was asked for, and a video player that
    // asks for a bounded range (rather than for everything from an offset) gets one of those.
    static async Task copyRangeAsync(Stream source, Stream destination, long length, CancellationToken cancellationToken) {
        var buffer = new byte[(int)Math.Min(length, 81920)];
        while (length > 0) {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), cancellationToken);
            if (read <= 0) break; // the file is shorter than its own length said; nothing left to send
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            length -= read;
        }
    }
}