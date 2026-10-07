using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relatude.DB.AI;
using Relatude.DB.Common;

namespace Relatude.DB.Http;

/// <summary>A file a call names: its bytes, and the SHA-256 the service knows it by, in lowercase hex.</summary>
internal sealed record ServiceFile(byte[] Bytes, string Sha256) {
    public static ServiceFile Of(byte[] bytes, string parameterName) {
        ArgumentNullException.ThrowIfNull(bytes, parameterName);
        if (bytes.Length == 0) throw new ArgumentException("The file is empty. ", parameterName);
        return new(bytes, Sha256Of(bytes));
    }
    public static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>
/// The HTTP side every Relatude service that works on files shares - Imaging and FileToText today -
/// over plain HttpClient: the license API key as the bearer token, files named by their SHA-256 and
/// sent before the call that names them, and refusals turned into <see cref="RelatudeServiceException"/>.
/// <para><b>Files.</b> The service keeps every file it is sent under its SHA-256, for the license that
/// sent it, so a file is sent once and named from then on. One no larger than the service's part size
/// (10 MB on the hosted services) goes whole, to <c>PUT files/{sha256}</c> with
/// <c>Expect: 100-continue</c>, so a file the service has already is answered before the body leaves.
/// A larger one goes in parts through <c>uploads</c>, three side by side; starting the same upload
/// again gives back the one the service has, with only the parts it still misses. Sending files costs
/// nothing and repeats nothing, so the usual retries apply. This client remembers which files it has
/// seen the service hold for the key, answers included, so a file is not even offered twice; and
/// when the service has let one go after all, the call is answered 409 with the file named - nothing
/// is charged then - and the file is sent again and the call made again.</para>
/// <para><b>Calls.</b> A call is paid for before the service hands it to a provider, and the credits
/// are never given back, so it is repeated only on the two answers the service gives before any money
/// moves: 429, rate limited, and 503, the license server out of reach. Any other failure - a provider
/// failing, a timeout, a dropped connection - may already have been charged, so it is thrown rather
/// than retried, and <see cref="RelatudeServiceException.MayHaveBeenCharged"/> says which it was.</para>
/// </summary>
internal sealed class RelatudeServiceClient : IDisposable {
    /// <summary>What one request carries until the service says otherwise: its Files:PartBytes, 10 MB on the hosted services.</summary>
    internal const int DefaultPartBytes = 10 * 1024 * 1024;
    const int _maxKnownFiles = 4096;
    const int _partsSideBySide = 3;
    const string _hit = "hit";

    readonly HttpClient _http;
    // what the service offers is a small answer that costs nothing: it is not worth the minutes a call
    // may take, and a settings page or a test panel asking an address that never answers should hear so soon
    readonly HttpClient _lookupHttp = new() { Timeout = _lookupTimeout };
    static readonly TimeSpan _lookupTimeout = TimeSpan.FromSeconds(15);
    readonly string _api;
    readonly string _serviceName;
    readonly Func<string?> _apiKey;
    readonly string _noKeyMessage;
    readonly TimeSpan _timeout;

    // the files this client has seen the service hold for one key, least lately used first
    readonly object _knownLock = new();
    readonly LinkedList<string> _knownOrder = new();
    readonly Dictionary<string, LinkedListNode<string>> _known = new(StringComparer.Ordinal);
    string? _knownFor;

    // the most one request may carry, learned from the service the first time it answers an upload
    int _partBytes = DefaultPartBytes;

    /// <param name="serviceName">The service as people know it, for messages: "Relatude Imaging service".</param>
    /// <param name="apiRoot">Where the service's routes are: its root and its prefix, such as https://imaging.services.relatude.com/api/imaging.</param>
    /// <param name="apiKey">The key a call is charged to, asked for at every call. Null or empty when there is none.</param>
    /// <param name="noKeyMessage">What a call says when there is no key, naming where one is set.</param>
    /// <param name="timeout">How long one request may take: a call to a provider that reads or draws, or one part of a file.</param>
    public RelatudeServiceClient(string serviceName, string apiRoot, Func<string?> apiKey, string noKeyMessage, TimeSpan timeout) {
        _serviceName = serviceName;
        _api = apiRoot.TrimEnd('/');
        _apiKey = apiKey;
        _noKeyMessage = noKeyMessage;
        _timeout = timeout;
        _http = new HttpClient() { Timeout = timeout };
    }

    /// <summary>The key a call is charged to, or null when there is none.</summary>
    public string? ApiKeyOrNull() {
        var key = _apiKey();
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    string apiKey() => ApiKeyOrNull() ?? throw new InvalidOperationException(_noKeyMessage);

    static void authorize(HttpRequestMessage request, string? key) {
        if (key != null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
    }

    // ------------------------------------------------------------------ calls

    /// <summary>
    /// Makes a charged call: the files it names are sent first unless the service is known to hold
    /// them, then the call is posted, and a 409 naming files the service has let go has them sent
    /// again and the call made again. Returns the successful answer, which the caller disposes, with
    /// the key it was made with; anything else is thrown as a <see cref="RelatudeServiceException"/>.
    /// </summary>
    public async Task<(HttpResponseMessage Response, string Key)> PostAsync(string operation, string jsonBody, bool fresh, IReadOnlyList<ServiceFile> files, CancellationToken cancellationToken) {
        var key = apiKey(); // once, before anything is sent: every request of the call goes with the same key
        var url = _api + "/" + operation;
        var named = files.DistinctBy(f => f.Sha256).ToArray();
        foreach (var file in named) {
            if (!isKnown(key, file.Sha256)) await sendFileAsync(key, file, cancellationToken);
        }
        // twice at most: a file let go between the upload and the call is rare, twice in a row rarer still
        for (var round = 0; ; round++) {
            var response = await sendAsync(url, () => HttpRetry.SendAsync(_http, () => {
                var request = new HttpRequestMessage(HttpMethod.Post, url) {
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
                };
                // a new answer rather than the one kept for the same call, made and paid for again
                if (fresh) request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                authorize(request, key);
                return request;
            }, retryOnTimeout: false, isTransient: answeredBeforeCharging, retryOnConnectionError: false, cancellationToken: cancellationToken), cancellationToken);
            if (response.IsSuccessStatusCode) return (response, key);
            using (response) {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == HttpStatusCode.Conflict && round < 2) {
                    var missing = readMissing(body);
                    var resend = named.Where(f => missing.Contains(f.Sha256)).ToArray();
                    if (resend.Length > 0) {
                        foreach (var file in resend) {
                            forget(file.Sha256);
                            await sendFileAsync(key, file, cancellationToken);
                        }
                        continue;
                    }
                }
                throw refusal(url, response.StatusCode, body);
            }
        }
    }

    /// <summary>
    /// A call that costs nothing and needs no license - what the service offers, what it costs -
    /// answered as JSON. The key goes with it when there is one, which lets a self-hosted deployment
    /// require it. The usual retries apply.
    /// </summary>
    public async Task<string> GetAsync(string path, CancellationToken cancellationToken) {
        var url = _api + "/" + path;
        var key = ApiKeyOrNull();
        using var response = await sendAsync(url, () => HttpRetry.SendAsync(_lookupHttp, () => {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            authorize(request, key);
            return request;
        }, retryOnTimeout: false, cancellationToken: cancellationToken), cancellationToken, _lookupTimeout);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw refusal(url, response.StatusCode, body);
        return body;
    }

    /// <summary>Remembers that the service holds a file for the key, as it does every answer it gives: a later call naming it sends nothing first.</summary>
    public void MarkHeld(string key, string sha256) => markKnown(key, sha256.ToLowerInvariant());

    /// <summary>
    /// The failures a charged call may be repeated on: 429, rate limited with the Retry-After to wait,
    /// and 503, the license server out of reach. The service gives both before it takes any credits.
    /// None of the others <see cref="HttpRetry.IsTransient"/> would repeat is safe: a 500, 502 or 504
    /// can come after the call was charged, and a 408 is not among the service's answers, so it says
    /// nothing about how far the request got.
    /// </summary>
    static bool answeredBeforeCharging(int statusCode) => statusCode is 429 or 503;

    // ------------------------------------------------------------------ files

    /// <summary>Sends one file: whole when one request may carry it, in parts when not.</summary>
    async Task sendFileAsync(string key, ServiceFile file, CancellationToken cancellationToken) {
        if (file.Bytes.Length <= Volatile.Read(ref _partBytes)) {
            var url = _api + "/files/" + file.Sha256;
            using var response = await sendAsync(url, () => HttpRetry.SendAsync(_http, () => {
                var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = octets(file.Bytes, 0, file.Bytes.Length) };
                // a file the service has already for this key is answered 200 before the body goes
                request.Headers.ExpectContinue = true;
                authorize(request, key);
                return request;
            }, retryOnTimeout: false, cancellationToken: cancellationToken), cancellationToken);
            if (response.IsSuccessStatusCode) {
                markKnown(key, file.Sha256);
                return;
            }
            // this deployment takes less in one request than assumed: the upload in parts says how much
            if (response.StatusCode != HttpStatusCode.RequestEntityTooLarge) {
                throw refusal(url, response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
            }
        }
        await sendInPartsAsync(key, file, cancellationToken);
    }

    sealed record UploadInfo(Guid Id, string? Sha256, long Size, int PartBytes, int Parts, int[]? Missing);

    static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Sends a file in parts: the upload started (or the same one taken up again), every part it
    /// misses sent, three side by side, and the upload completed, which has the service put the file
    /// together and check its SHA-256. Parts the service still misses at the end are sent again, twice
    /// at most.
    /// </summary>
    async Task sendInPartsAsync(string key, ServiceFile file, CancellationToken cancellationToken) {
        var startUrl = _api + "/uploads";
        var startBody = $$"""{"sha256":"{{file.Sha256}}","size":{{file.Bytes.LongLength}}}""";
        string started;
        HttpStatusCode startStatus;
        using (var response = await sendAsync(startUrl, () => HttpRetry.SendAsync(_http, () => {
            var request = new HttpRequestMessage(HttpMethod.Post, startUrl) { Content = new StringContent(startBody, Encoding.UTF8, "application/json") };
            authorize(request, key);
            return request;
        }, cancellationToken: cancellationToken), cancellationToken)) {
            started = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) throw refusal(startUrl, response.StatusCode, started);
            startStatus = response.StatusCode;
        }
        // 200: the service has the file for this key already, and no upload was started
        if (startStatus != HttpStatusCode.Created) {
            markKnown(key, file.Sha256);
            return;
        }
        var upload = parseUpload(started, startUrl);
        if (upload.PartBytes > 0) Volatile.Write(ref _partBytes, upload.PartBytes);
        var uploadUrl = startUrl + "/" + upload.Id.ToString("D");
        var missing = upload.Missing ?? [.. Enumerable.Range(0, upload.Parts)];
        for (var round = 0; ; round++) {
            await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = _partsSideBySide, CancellationToken = cancellationToken }, async (index, token) => {
                var offset = (long)index * upload.PartBytes;
                if (index < 0 || offset >= file.Bytes.LongLength) throw new RelatudeServiceException(0, $"The {_serviceName} asked for part {index} of a file of {file.Bytes.LongLength} bytes in parts of {upload.PartBytes}. ");
                var length = (int)Math.Min(upload.PartBytes, file.Bytes.LongLength - offset);
                var partUrl = uploadUrl + "/parts/" + index;
                using var response = await sendAsync(partUrl, () => HttpRetry.SendAsync(_http, () => {
                    var request = new HttpRequestMessage(HttpMethod.Put, partUrl) { Content = octets(file.Bytes, (int)offset, length) };
                    authorize(request, key);
                    return request;
                }, retryOnTimeout: false, cancellationToken: token), token);
                if (!response.IsSuccessStatusCode) throw refusal(partUrl, response.StatusCode, await response.Content.ReadAsStringAsync(token));
            });
            var completeUrl = uploadUrl + "/complete";
            // not repeated by itself: a completion whose answer was lost has used the upload up, and is
            // told apart from a failed one below by asking whether the file is there
            using var completed = await sendAsync(completeUrl, () => HttpRetry.SendAsync(_http, () => {
                var request = new HttpRequestMessage(HttpMethod.Post, completeUrl);
                authorize(request, key);
                return request;
            }, retryOnTimeout: false, isTransient: _ => false, retryOnConnectionError: false, cancellationToken: cancellationToken), cancellationToken);
            if (completed.IsSuccessStatusCode) {
                markKnown(key, file.Sha256);
                return;
            }
            var body = await completed.Content.ReadAsStringAsync(cancellationToken);
            // parts the service did not get after all: ask which, and send those
            if (completed.StatusCode == HttpStatusCode.Conflict && round < 2) {
                missing = parseUpload(await GetUploadAsync(uploadUrl, key, cancellationToken), uploadUrl).Missing ?? [];
                if (missing.Length > 0) continue;
            }
            // the upload is gone: completed by an earlier attempt whose answer was lost, or given up
            if (completed.StatusCode == HttpStatusCode.NotFound && await holdsAsync(key, file.Sha256, cancellationToken)) {
                markKnown(key, file.Sha256);
                return;
            }
            throw refusal(completeUrl, completed.StatusCode, body);
        }
    }

    async Task<string> GetUploadAsync(string uploadUrl, string key, CancellationToken cancellationToken) {
        using var response = await sendAsync(uploadUrl, () => HttpRetry.SendAsync(_http, () => {
            var request = new HttpRequestMessage(HttpMethod.Get, uploadUrl);
            authorize(request, key);
            return request;
        }, cancellationToken: cancellationToken), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw refusal(uploadUrl, response.StatusCode, body);
        return body;
    }

    /// <summary>Whether the service has the file for this key: <c>HEAD files/{sha256}</c>.</summary>
    async Task<bool> holdsAsync(string key, string sha256, CancellationToken cancellationToken) {
        var url = _api + "/files/" + sha256;
        using var response = await sendAsync(url, () => HttpRetry.SendAsync(_http, () => {
            var request = new HttpRequestMessage(HttpMethod.Head, url);
            authorize(request, key);
            return request;
        }, cancellationToken: cancellationToken), cancellationToken);
        return response.IsSuccessStatusCode;
    }

    UploadInfo parseUpload(string json, string url) {
        try {
            return JsonSerializer.Deserialize<UploadInfo>(json, _json) ?? throw new JsonException("The body is empty.");
        } catch (JsonException ex) {
            throw new RelatudeServiceException(0, $"The {_serviceName} at {url} answered with something other than an upload: {OpenAIWire.Truncate(json, 200)}", null, ex);
        }
    }

    static ByteArrayContent octets(byte[] bytes, int offset, int count) {
        var content = new ByteArrayContent(bytes, offset, count);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    // ------------------------------------------------------------------ the files known to be held

    bool isKnown(string key, string sha256) {
        lock (_knownLock) {
            if (_knownFor != key || !_known.TryGetValue(sha256, out var node)) return false;
            _knownOrder.Remove(node);
            _knownOrder.AddLast(node);
            return true;
        }
    }

    void markKnown(string key, string sha256) {
        lock (_knownLock) {
            // what one license holds says nothing of another: a new key starts from nothing
            if (_knownFor != key) {
                _known.Clear();
                _knownOrder.Clear();
                _knownFor = key;
            }
            if (_known.TryGetValue(sha256, out var node)) {
                _knownOrder.Remove(node);
                _knownOrder.AddLast(node);
                return;
            }
            _known[sha256] = _knownOrder.AddLast(sha256);
            while (_known.Count > _maxKnownFiles) {
                _known.Remove(_knownOrder.First!.Value);
                _knownOrder.RemoveFirst();
            }
        }
    }

    void forget(string sha256) {
        lock (_knownLock) {
            if (_known.Remove(sha256, out var node)) _knownOrder.Remove(node);
        }
    }

    // ------------------------------------------------------------------ answers

    /// <summary>A header of the answer, wherever HttpClient put it.</summary>
    public static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values) ? values.FirstOrDefault() : null;

    public static int IntHeader(HttpResponseMessage response, string name) =>
        int.TryParse(Header(response, name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>Whether the answer is one the service made before for the same call: <c>X-Cache: hit</c>.</summary>
    public static bool WasCached(HttpResponseMessage response) => string.Equals(Header(response, "X-Cache"), _hit, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads an answer as JSON, saying what it was meant to be when it is not.</summary>
    public T Parse<T>(string json, string path, string what) where T : class {
        try {
            return JsonSerializer.Deserialize<T>(json, _json) ?? throw new JsonException("The body is empty.");
        } catch (JsonException ex) {
            throw new RelatudeServiceException(0, $"The {_serviceName} at {_api}/{path} answered with something other than {what}: {OpenAIWire.Truncate(json, 200)}", null, ex);
        }
    }

    /// <summary>
    /// Sends a request, turning what HttpClient throws when no answer came - a timeout, a dropped
    /// connection - into a <see cref="RelatudeServiceException"/> with no status. A cancellation the
    /// caller asked for goes on as it is.
    /// </summary>
    async Task<HttpResponseMessage> sendAsync(string url, Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken, TimeSpan? timeout = null) {
        try {
            return await send();
        } catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) {
            throw new RelatudeServiceException(0, $"The {_serviceName} at {url} did not answer within {(timeout ?? _timeout).TotalSeconds:0} seconds. ", null, ex);
        } catch (HttpRequestException ex) {
            throw new RelatudeServiceException(0, $"The {_serviceName} at {url} could not be reached: {ex.Message}", null, ex);
        }
    }

    /// <summary>A refusal, with the service's own reason when it sent one and the raw body when it did not.</summary>
    RelatudeServiceException refusal(string url, HttpStatusCode status, string body) {
        var reason = readError(body);
        return new RelatudeServiceException((int)status, $"The {_serviceName} at {url} returned {(int)status} {status}: {reason}", reason);
    }

    static string readError(string body) {
        try {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String) {
                return error.GetString() ?? "";
            }
        } catch (JsonException) { // not JSON, fall through to the raw body
        }
        return OpenAIWire.Truncate(body, 500);
    }

    /// <summary>The files a 409 names as missing, in lowercase as this client names them.</summary>
    static HashSet<string> readMissing(string body) {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        try {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("missing", out var list)
                && list.ValueKind == JsonValueKind.Array) {
                foreach (var item in list.EnumerateArray()) {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } sha) missing.Add(sha.Trim().ToLowerInvariant());
                }
            }
        } catch (JsonException) { // not the service's own refusal: nothing is named
        }
        return missing;
    }

    public void Dispose() {
        _http.Dispose();
        _lookupHttp.Dispose();
    }
}
