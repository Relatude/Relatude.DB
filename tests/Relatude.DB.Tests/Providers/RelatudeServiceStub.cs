using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Relatude.Providers;

/// <summary>
/// A stand-in for a Relatude service that works on files - Imaging or FileToText - holding to the
/// file protocol the real ones share (Relatude.DB.Services/ServiceFiles): a file kept under the
/// SHA-256 it was sent to and only when its bytes hash to it, a whole file at most
/// <see cref="PartBytes"/> long and a larger one in parts, a PUT of a file it has answered before its
/// body is read, and a call naming a file it does not have answered 409 with the file in "missing" -
/// nothing scripted is used up by that. Every other route answers with what was scripted, in order.
/// Every request is recorded, its body as bytes.
/// </summary>
sealed class RelatudeServiceStub : IAsyncDisposable {
    public sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body) {
        public string Text => Encoding.UTF8.GetString(Body);
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }
    sealed record Answer(int Status, byte[] Body, string ContentType, (string Name, string Value)[] Headers);
    sealed class Upload(string sha256, long size, int partBytes) {
        public string Sha256 { get; } = sha256;
        public long Size { get; } = size;
        public int PartBytes { get; } = partBytes;
        public int Parts => (int)((Size + PartBytes - 1) / PartBytes);
        public readonly ConcurrentDictionary<int, byte[]> Received = new();
        public int[] Missing => [.. Enumerable.Range(0, Parts).Where(i => !Received.ContainsKey(i))];
    }

    readonly WebApplication _app;
    readonly string _prefix;
    readonly Queue<Answer> _answers = new();
    readonly ConcurrentDictionary<Guid, Upload> _uploads = new();
    public readonly List<Request> Requests = [];
    /// <summary>The files the service holds, by SHA-256.</summary>
    public readonly ConcurrentDictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The most one request may carry, a whole file or one part: Files:PartBytes.</summary>
    public int PartBytes { get; set; } = 10 * 1024 * 1024;
    /// <summary>A part to drop once, as if it never arrived, so a completion finds it missing.</summary>
    public int? LosePartOnce { get; set; }
    public string BaseUrl { get; private set; } = "";

    RelatudeServiceStub(WebApplication app, string prefix) {
        _app = app;
        _prefix = prefix;
    }

    /// <param name="service">The service's prefix: "imaging", "filetotext" or "translation".</param>
    public static async Task<RelatudeServiceStub> StartAsync(string service) {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var stub = new RelatudeServiceStub(app, "/api/" + service);
        app.Run(stub.handleAsync);
        await app.StartAsync();
        stub.BaseUrl = app.Urls.First().TrimEnd('/');
        return stub;
    }

    public void EnqueueJson(int status, string json, params (string Name, string Value)[] headers) {
        lock (_answers) _answers.Enqueue(new(status, Encoding.UTF8.GetBytes(json), "application/json", headers));
    }

    public void EnqueueImage(byte[] png, params (string Name, string Value)[] headers) {
        lock (_answers) _answers.Enqueue(new(200, png, "image/png", headers));
    }

    /// <summary>A 204: the headers, and no body.</summary>
    public void EnqueueNoContent(params (string Name, string Value)[] headers) {
        lock (_answers) _answers.Enqueue(new(204, [], "", headers));
    }

    public Request Single() {
        lock (Requests) {
            Assert.AreEqual(1, Requests.Count, "expected exactly one request");
            return Requests[0];
        }
    }

    public Request[] Calls(string method, string pathStart) {
        lock (Requests) return [.. Requests.Where(r => r.Method == method && r.Path.StartsWith(_prefix + pathStart, StringComparison.Ordinal))];
    }

    /// <summary>The requests that were not about files: the calls themselves.</summary>
    public Request[] Operations() {
        lock (Requests) return [.. Requests.Where(r => !r.Path.StartsWith(_prefix + "/files/", StringComparison.Ordinal) && !r.Path.StartsWith(_prefix + "/uploads", StringComparison.Ordinal))];
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The start of a PNG - the signature and the header chunk with its size - and some bytes that differ by seed.</summary>
    public static byte[] FakePng(int width, int height, int seed = 0, int extra = 32) {
        var png = new byte[33 + extra];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), (uint)height);
        for (var i = 33; i < png.Length; i++) png[i] = (byte)(seed * 31 + i);
        return png;
    }

    async Task handleAsync(HttpContext context) {
        var request = context.Request;
        var path = request.Path.Value ?? "";
        var headers = request.Headers.ToDictionary(h => h.Key, h => (string)h.Value!, StringComparer.OrdinalIgnoreCase);
        var relative = path.StartsWith(_prefix, StringComparison.Ordinal) ? path[_prefix.Length..] : path;
        var segments = relative.Trim('/').Split('/');

        // a file the caller has already is answered before the body is read, as the service does,
        // so a client sending Expect: 100-continue never sends it
        if (request.Method == "PUT" && segments is ["files", var known] && Files.TryGetValue(known, out var held)) {
            record(new(request.Method, path, headers, []));
            await json(context, 200, $$"""{"sha256":"{{known.ToLowerInvariant()}}","size":{{held.Length}}}""");
            return;
        }
        if (request.Method == "PUT" && request.ContentLength > PartBytes) {
            record(new(request.Method, path, headers, []));
            await json(context, 413, $$"""{"error":"A request carries at most {{PartBytes}} bytes: send a larger file in parts."}""");
            return;
        }

        byte[] body;
        using (var buffer = new MemoryStream()) {
            await request.Body.CopyToAsync(buffer);
            body = buffer.ToArray();
        }
        var recorded = new Request(request.Method, path, headers, body);
        record(recorded);

        switch (request.Method, segments) {
            case ("HEAD", ["files", var sha]):
                context.Response.StatusCode = Files.ContainsKey(sha) ? 200 : 404;
                return;
            case ("PUT", ["files", var sha]):
                if (Sha256(body) != sha.ToLowerInvariant()) {
                    await json(context, 422, """{"error":"The file sent is not the one its SHA-256 names, and was not kept."}""");
                    return;
                }
                Files[sha] = body;
                await json(context, 201, $$"""{"sha256":"{{sha.ToLowerInvariant()}}","size":{{body.Length}}}""");
                return;
            case ("POST", ["uploads"]): {
                    var start = recorded.Json;
                    var sha = start.GetProperty("sha256").GetString()!.ToLowerInvariant();
                    var size = start.GetProperty("size").GetInt64();
                    if (Files.TryGetValue(sha, out var file)) {
                        await json(context, 200, $$"""{"sha256":"{{sha}}","size":{{file.Length}}}""");
                        return;
                    }
                    // the same file and size started again is the same upload
                    var existing = _uploads.FirstOrDefault(u => u.Value.Sha256 == sha && u.Value.Size == size);
                    var id = existing.Value != null ? existing.Key : Guid.NewGuid();
                    var upload = existing.Value ?? _uploads.GetOrAdd(id, new Upload(sha, size, PartBytes));
                    await json(context, 201, uploadJson(id, upload));
                    return;
                }
            case ("GET", ["uploads", var id]):
                if (!_uploads.TryGetValue(Guid.Parse(id), out var status)) {
                    await json(context, 404, """{"error":"There is no such upload."}""");
                    return;
                }
                await json(context, 200, uploadJson(Guid.Parse(id), status));
                return;
            case ("PUT", ["uploads", var id, "parts", var index]): {
                    if (!_uploads.TryGetValue(Guid.Parse(id), out var upload)) {
                        await json(context, 404, """{"error":"There is no such upload."}""");
                        return;
                    }
                    var part = int.Parse(index);
                    if (LosePartOnce == part) LosePartOnce = null;
                    else upload.Received[part] = body;
                    context.Response.StatusCode = 204;
                    return;
                }
            case ("POST", ["uploads", var id, "complete"]): {
                    if (!_uploads.TryGetValue(Guid.Parse(id), out var upload)) {
                        await json(context, 404, """{"error":"There is no such upload."}""");
                        return;
                    }
                    if (upload.Missing.Length > 0) {
                        await json(context, 409, $$"""{"error":"Parts {{string.Join(", ", upload.Missing)}} are missing."}""");
                        return;
                    }
                    var whole = upload.Received.OrderBy(p => p.Key).SelectMany(p => p.Value).ToArray();
                    _uploads.TryRemove(Guid.Parse(id), out _);
                    if (Sha256(whole) != upload.Sha256) {
                        await json(context, 422, """{"error":"The parts are not the file its SHA-256 names. The upload was cancelled."}""");
                        return;
                    }
                    Files[upload.Sha256] = whole;
                    await json(context, 201, $$"""{"sha256":"{{upload.Sha256}}","size":{{whole.Length}}}""");
                    return;
                }
        }

        // a call naming files the service does not have is answered 409 before anything else, as the
        // service does, and nothing is charged for it
        if (request.Method == "POST" && body.Length > 0) {
            var missing = namedFiles(recorded.Json).Where(sha => !Files.ContainsKey(sha)).Distinct().ToArray();
            if (missing.Length > 0) {
                await json(context, 409, JsonSerializer.Serialize(new { error = "Send these files first.", missing }));
                return;
            }
        }
        Answer? answer;
        lock (_answers) answer = _answers.Count > 0 ? _answers.Dequeue() : null;
        if (answer == null) {
            await json(context, 500, """{"error":"no scripted answer left"}""");
            return;
        }
        context.Response.StatusCode = answer.Status;
        if (answer.ContentType.Length > 0) context.Response.ContentType = answer.ContentType;
        foreach (var (name, value) in answer.Headers) context.Response.Headers[name] = value;
        await context.Response.Body.WriteAsync(answer.Body);
    }

    /// <summary>The fields the two services name files in.</summary>
    static IEnumerable<string> namedFiles(JsonElement call) {
        if (call.ValueKind != JsonValueKind.Object) yield break;
        foreach (var name in new[] { "image", "mask", "file" }) {
            if (call.TryGetProperty(name, out var one) && one.ValueKind == JsonValueKind.String) yield return one.GetString()!.ToLowerInvariant();
        }
        foreach (var name in new[] { "inspiration", "references" }) {
            if (!call.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in list.EnumerateArray()) yield return item.GetString()!.ToLowerInvariant();
        }
    }

    static string uploadJson(Guid id, Upload upload) => JsonSerializer.Serialize(new {
        id, sha256 = upload.Sha256, size = upload.Size, partBytes = upload.PartBytes, parts = upload.Parts, missing = upload.Missing, expiresUtc = DateTime.UtcNow.AddHours(1),
    });

    void record(Request request) {
        lock (Requests) Requests.Add(request);
    }

    static async Task json(HttpContext context, int status, string body) {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body);
    }

    public async ValueTask DisposeAsync() {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
