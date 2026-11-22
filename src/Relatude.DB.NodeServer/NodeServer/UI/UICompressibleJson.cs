using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Net.Http.Headers;
using Relatude.DB.NodeServer.Json;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// A command's answer as JSON, compressed with Brotli or gzip when it is large and the browser accepts
/// either. A whole data model is a megabyte of repetitive JSON that shrinks ten-fold or more; the fastest
/// level costs a few milliseconds of CPU and is most of what there is to gain. The value stays readable
/// (<see cref="IValueHttpResult"/>), like the plain JSON result's.
/// </summary>
public sealed class UICompressibleJson(object? value) : IResult, IValueHttpResult, IStatusCodeHttpResult {
    /// <summary>Below this the answer goes as it is: compressing a few kilobytes saves less than it costs.</summary>
    public const int MinimumBytes = 8 * 1024;
    const string contentType = "application/json; charset=utf-8";
    public object? Value => value;
    public int? StatusCode => StatusCodes.Status200OK;

    public async Task ExecuteAsync(HttpContext http) {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, RelatudeDBJsonOptions.Default);
        var encoding = json.Length < MinimumBytes ? null : AcceptedEncoding(http.Request.Headers.AcceptEncoding.ToString());
        var response = http.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = contentType;
        if (json.Length >= MinimumBytes) response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
        var body = encoding == null ? json : Compress(json, encoding);
        if (encoding != null) response.Headers.ContentEncoding = encoding;
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, http.RequestAborted);
    }

    public static byte[] Compress(byte[] data, string encoding) {
        using var buffer = new MemoryStream(data.Length / 8 + 64);
        using (Stream zip = encoding == "br"
            ? new BrotliStream(buffer, CompressionLevel.Fastest, leaveOpen: true)
            : new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) {
            zip.Write(data);
        }
        return buffer.ToArray();
    }

    /// <summary>"br" or "gzip" as an Accept-Encoding header allows them, Brotli first; null for neither.</summary>
    public static string? AcceptedEncoding(string? acceptEncoding) {
        if (string.IsNullOrWhiteSpace(acceptEncoding)) return null;
        bool br = false, gzip = false;
        foreach (var part in acceptEncoding.Split(',')) {
            var pieces = part.Split(';');
            var name = pieces[0].Trim();
            if (pieces.Skip(1).Any(refuses)) continue;
            if (name.Equals("br", StringComparison.OrdinalIgnoreCase)) br = true;
            else if (name.Equals("gzip", StringComparison.OrdinalIgnoreCase)) gzip = true;
        }
        return br ? "br" : gzip ? "gzip" : null;
        static bool refuses(string parameter) {
            var p = parameter.Trim();
            return p.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(p[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var q) && q <= 0;
        }
    }
}
