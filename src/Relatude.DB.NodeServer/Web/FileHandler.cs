using Microsoft.Net.Http.Headers;

namespace Relatude.DB.Web;

public static class FileHandler {
    public static async Task<IResult> HandleFileAsync(HttpContext http, Stream? stream, string? fileName = null, bool? attachment = null, string? contentType = null, bool? cached = null) {
        var totalLength = stream.CanSeek ? stream.Length : (long?)null;
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
        if (stream.CanSeek && !string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=")) {
            try {
                var range = rangeHeader["bytes=".Length..].Split('-');
                var start = long.TryParse(range[0], out var s) ? s : 0;
                var end = range.Length > 1 && long.TryParse(range[1], out var e) ? e : totalLength!.Value - 1;
                end = Math.Min(end, totalLength!.Value - 1);
                var length = end - start + 1;
                stream.Seek(start, SeekOrigin.Begin);
                http.Response.StatusCode = 206;
                http.Response.Headers.ContentRange = $"bytes {start}-{end}/{totalLength}";
                http.Response.Headers.AcceptRanges = "bytes";
                http.Response.ContentLength = length;
                if (contentType != null) http.Response.ContentType = contentType;
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