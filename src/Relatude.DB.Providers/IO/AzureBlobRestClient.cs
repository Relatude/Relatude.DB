using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Relatude.DB.Http;

namespace Relatude.DB.IO;

public class AzureBlobRequestException(int statusCode, string? errorCode, string message) : Exception(message) {
    public int StatusCode { get; } = statusCode;
    /// <summary>The x-ms-error-code header of the response, e.g. "BlobNotFound" or "LeaseAlreadyPresent". </summary>
    public string? ErrorCode { get; } = errorCode;
}
internal class BlobListItem {
    public required string Name;
    public long ContentLength;
    public DateTime LastModifiedUtc;
    public DateTime CreatedOnUtc;
}
internal class BlobProperties {
    public long ContentLength;
    public DateTime LastModifiedUtc;
    public DateTime CreatedOnUtc;
}
/// <summary>
/// Minimal Azure Blob Storage REST client covering the operations the IO provider needs:
/// list, properties, append blobs, ranged downloads, delete and leases. Auth is SharedKey
/// (account key) or SAS, both parsed from a standard storage connection string.
/// Everything runs over plain HttpClient, no Azure SDK involved.
/// </summary>
internal class AzureBlobRestClient : IDisposable {
    const string _xmsVersion = "2023-11-03"; // >= 2022-11-02, required for append blocks over 4MB
    readonly HttpClient _http;
    readonly string _accountName;
    readonly byte[]? _accountKey;                      // SharedKey auth when set...
    readonly KeyValuePair<string, string>[]? _sas;     // ...otherwise SAS query parameters
    readonly string _containerUrl;
    readonly string _canonicalContainerPath;           // path part used in the string to sign
    public AzureBlobRestClient(string connectionString, string containerName) {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("Blob connection string is required. ");
        if (string.IsNullOrWhiteSpace(containerName)) throw new ArgumentException("Blob container name is required. ");
        parseConnectionString(connectionString, out _accountName, out _accountKey, out _sas, out var blobEndpoint);
        _containerUrl = blobEndpoint.TrimEnd('/') + "/" + Uri.EscapeDataString(containerName);
        _canonicalContainerPath = new Uri(_containerUrl).AbsolutePath;
        _http = new HttpClient() { Timeout = TimeSpan.FromMinutes(10) };
    }
    static void parseConnectionString(string connectionString, out string accountName, out byte[]? accountKey, out KeyValuePair<string, string>[]? sas, out string blobEndpoint) {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in connectionString.Split(';')) {
            if (string.IsNullOrWhiteSpace(part)) continue;
            var eq = part.IndexOf('='); // only the first =, account keys are base64 with trailing =
            if (eq < 1) throw new ArgumentException("Invalid blob connection string. ");
            values[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        if (values.TryGetValue("UseDevelopmentStorage", out var dev) && dev.Equals("true", StringComparison.OrdinalIgnoreCase)) {
            // the well known Azurite / Storage Emulator account
            accountName = "devstoreaccount1";
            accountKey = Convert.FromBase64String("Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==");
            sas = null;
            blobEndpoint = "http://127.0.0.1:10000/devstoreaccount1";
            return;
        }
        values.TryGetValue("AccountName", out var name);
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("The blob connection string has no AccountName. ");
        accountName = name;
        if (values.TryGetValue("AccountKey", out var key) && !string.IsNullOrEmpty(key)) {
            accountKey = Convert.FromBase64String(key);
            sas = null;
        } else if (values.TryGetValue("SharedAccessSignature", out var sasString) && !string.IsNullOrEmpty(sasString)) {
            accountKey = null;
            sas = sasString.TrimStart('?').Split('&')
                .Select(p => p.Split('=', 2))
                .Select(p => new KeyValuePair<string, string>(Uri.UnescapeDataString(p[0]), p.Length > 1 ? Uri.UnescapeDataString(p[1]) : ""))
                .ToArray();
        } else {
            throw new ArgumentException("The blob connection string has neither an AccountKey nor a SharedAccessSignature. ");
        }
        if (values.TryGetValue("BlobEndpoint", out var endpoint) && !string.IsNullOrEmpty(endpoint)) {
            blobEndpoint = endpoint;
        } else {
            values.TryGetValue("DefaultEndpointsProtocol", out var protocol);
            values.TryGetValue("EndpointSuffix", out var suffix);
            blobEndpoint = $"{(string.IsNullOrEmpty(protocol) ? "https" : protocol)}://{accountName}.blob.{(string.IsNullOrEmpty(suffix) ? "core.windows.net" : suffix)}";
        }
    }

    // request building and signing
    class Request {
        public required HttpMethod Method;
        public string? BlobName;                                          // null targets the container
        public List<KeyValuePair<string, string>> Query = [];             // unencoded values
        public SortedDictionary<string, string> XmsHeaders = new(StringComparer.Ordinal);
        public byte[]? Content;
        public int ContentLength;
        public bool IfNoneMatchAny;                                       // If-None-Match: * (create if not exists)
    }
    static string encodeBlobPath(string blobName) => string.Join('/', blobName.Split('/').Select(Uri.EscapeDataString));
    HttpRequestMessage build(Request r) {
        r.XmsHeaders["x-ms-date"] = DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture);
        r.XmsHeaders["x-ms-version"] = _xmsVersion;
        var url = new StringBuilder(_containerUrl);
        if (r.BlobName != null) url.Append('/').Append(encodeBlobPath(r.BlobName));
        var separator = '?';
        foreach (var q in r.Query) {
            url.Append(separator).Append(Uri.EscapeDataString(q.Key)).Append('=').Append(Uri.EscapeDataString(q.Value));
            separator = '&';
        }
        if (_sas != null) {
            foreach (var q in _sas) {
                url.Append(separator).Append(Uri.EscapeDataString(q.Key)).Append('=').Append(Uri.EscapeDataString(q.Value));
                separator = '&';
            }
        }
        var message = new HttpRequestMessage(r.Method, url.ToString());
        foreach (var h in r.XmsHeaders) message.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (r.IfNoneMatchAny) message.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        if (r.Content != null) {
            message.Content = new ByteArrayContent(r.Content, 0, r.ContentLength);
        } else if (r.Method == HttpMethod.Put) {
            message.Content = new ByteArrayContent([]); // ensures a Content-Length: 0 header
        }
        if (_accountKey != null) message.Headers.TryAddWithoutValidation("Authorization", sign(r));
        return message;
    }
    string sign(Request r) {
        var sb = new StringBuilder();
        sb.Append(r.Method.Method).Append('\n');
        sb.Append('\n');                                                          // Content-Encoding
        sb.Append('\n');                                                          // Content-Language
        var hasBody = r.Content != null && r.ContentLength > 0;
        sb.Append(hasBody ? r.ContentLength.ToString(CultureInfo.InvariantCulture) : "").Append('\n'); // Content-Length, empty when zero
        sb.Append('\n');                                                          // Content-MD5
        sb.Append('\n');                                                          // Content-Type
        sb.Append('\n');                                                          // Date, empty since x-ms-date is set
        sb.Append('\n');                                                          // If-Modified-Since
        sb.Append('\n');                                                          // If-Match
        sb.Append(r.IfNoneMatchAny ? "*" : "").Append('\n');                      // If-None-Match
        sb.Append('\n');                                                          // If-Unmodified-Since
        sb.Append('\n');                                                          // Range, empty since x-ms-range is used
        foreach (var h in r.XmsHeaders) sb.Append(h.Key).Append(':').Append(h.Value).Append('\n');
        sb.Append('/').Append(_accountName).Append(_canonicalContainerPath);
        if (r.BlobName != null) sb.Append('/').Append(encodeBlobPath(r.BlobName));
        foreach (var q in r.Query.OrderBy(q => q.Key, StringComparer.Ordinal)) {
            sb.Append('\n').Append(q.Key.ToLowerInvariant()).Append(':').Append(q.Value);
        }
        using var hmac = new HMACSHA256(_accountKey!);
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
        return $"SharedKey {_accountName}:{signature}";
    }
    static AzureBlobRequestException error(HttpResponseMessage response, string operation) {
        response.Headers.TryGetValues("x-ms-error-code", out var codes);
        var errorCode = codes?.FirstOrDefault();
        string body = "";
        try { body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult(); } catch { }
        if (body.Length > 500) body = body[..500] + "...";
        return new AzureBlobRequestException((int)response.StatusCode, errorCode,
            $"Azure blob {operation} failed with {(int)response.StatusCode} {response.StatusCode}" +
            (errorCode == null ? "" : $" ({errorCode})") + (body.Length == 0 ? ". " : $": {body}"));
    }

    // operations
    public void CreateContainerIfNotExists() {
        var r = new Request { Method = HttpMethod.Put, Query = { new("restype", "container") } };
        using var response = HttpRetry.Send(_http, () => build(r));
        if (response.IsSuccessStatusCode) return;
        var status = (int)response.StatusCode;
        if (status == 409) return;                      // already exists
        if (status == 403 || status == 401) return;     // no create permission (e.g. a blob-only SAS), assume it exists
        throw error(response, "create container");
    }
    public List<BlobListItem> ListBlobs(string? prefix) {
        var result = new List<BlobListItem>();
        string? marker = null;
        do {
            var r = new Request { Method = HttpMethod.Get, Query = { new("comp", "list"), new("restype", "container") } };
            if (!string.IsNullOrEmpty(prefix)) r.Query.Add(new("prefix", prefix));
            if (!string.IsNullOrEmpty(marker)) r.Query.Add(new("marker", marker));
            using var response = HttpRetry.Send(_http, () => build(r));
            if (!response.IsSuccessStatusCode) throw error(response, "list");
            var doc = XDocument.Load(response.Content.ReadAsStream());
            var root = doc.Root!;
            foreach (var blob in root.Element("Blobs")!.Elements("Blob")) {
                var properties = blob.Element("Properties")!;
                result.Add(new BlobListItem {
                    Name = blob.Element("Name")!.Value,
                    ContentLength = long.TryParse(properties.Element("Content-Length")?.Value, out var length) ? length : 0,
                    LastModifiedUtc = parseHttpDate(properties.Element("Last-Modified")?.Value),
                    CreatedOnUtc = parseHttpDate(properties.Element("Creation-Time")?.Value),
                });
            }
            marker = root.Element("NextMarker")?.Value;
        } while (!string.IsNullOrEmpty(marker));
        return result;
    }
    static DateTime parseHttpDate(string? value) {
        if (value != null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return d.UtcDateTime;
        return DateTime.MinValue;
    }
    public BlobProperties? GetProperties(string blobName) {
        var r = new Request { Method = HttpMethod.Head, BlobName = blobName };
        using var response = HttpRetry.Send(_http, () => build(r));
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw error(response, "get properties");
        response.Headers.TryGetValues("x-ms-creation-time", out var creation);
        return new BlobProperties {
            ContentLength = response.Content.Headers.ContentLength ?? 0,
            LastModifiedUtc = response.Content.Headers.LastModified?.UtcDateTime ?? DateTime.MinValue,
            CreatedOnUtc = parseHttpDate(creation?.FirstOrDefault()),
        };
    }
    public void CreateAppendBlobIfNotExists(string blobName) {
        var r = new Request { Method = HttpMethod.Put, BlobName = blobName, IfNoneMatchAny = true, XmsHeaders = { ["x-ms-blob-type"] = "AppendBlob" } };
        using var response = HttpRetry.Send(_http, () => build(r));
        if (response.IsSuccessStatusCode) return;
        if ((int)response.StatusCode == 409) return; // BlobAlreadyExists
        throw error(response, "create append blob");
    }
    Request appendBlockRequest(string blobName, byte[] data, int count, string? leaseId, long appendOffset) {
        var r = new Request {
            Method = HttpMethod.Put,
            BlobName = blobName,
            Query = { new("comp", "appendblock") },
            Content = data,
            ContentLength = count,
            // the append position guard makes retries safe: a retry of a request the server already
            // committed fails with AppendPositionConditionNotMet instead of appending twice
            XmsHeaders = { ["x-ms-blob-condition-appendpos"] = appendOffset.ToString(CultureInfo.InvariantCulture) }
        };
        if (leaseId != null) r.XmsHeaders["x-ms-lease-id"] = leaseId;
        return r;
    }
    public void AppendBlock(string blobName, byte[] data, int count, string? leaseId, long appendOffset) {
        using var response = HttpRetry.Send(_http, () => build(appendBlockRequest(blobName, data, count, leaseId, appendOffset)));
        verifyAppendResponse(response, blobName, count, appendOffset);
    }
    public async Task AppendBlockAsync(string blobName, byte[] data, int count, string? leaseId, long appendOffset) {
        using var response = await HttpRetry.SendAsync(_http, () => build(appendBlockRequest(blobName, data, count, leaseId, appendOffset)));
        verifyAppendResponse(response, blobName, count, appendOffset);
    }
    void verifyAppendResponse(HttpResponseMessage response, string blobName, int count, long appendOffset) {
        if (response.IsSuccessStatusCode) return;
        var ex = error(response, "append");
        if (ex.ErrorCode == "AppendPositionConditionNotMet") {
            // the block may have been committed by an attempt whose response was lost, verify by length
            var properties = GetProperties(blobName);
            if (properties != null && properties.ContentLength == appendOffset + count) return;
        }
        throw ex;
    }
    Request downloadRequest(string blobName, long position, int length, string? leaseId) {
        var r = new Request {
            Method = HttpMethod.Get,
            BlobName = blobName,
            XmsHeaders = { ["x-ms-range"] = $"bytes={position}-{position + length - 1}" }
        };
        if (leaseId != null) r.XmsHeaders["x-ms-lease-id"] = leaseId;
        return r;
    }
    public void DownloadRange(string blobName, long position, int length, string? leaseId, byte[] destination) {
        using var response = HttpRetry.Send(_http, () => build(downloadRequest(blobName, position, length, leaseId)));
        if (!response.IsSuccessStatusCode) throw error(response, "download");
        using var stream = response.Content.ReadAsStream();
        stream.ReadExactly(destination, 0, length);
    }
    public async Task DownloadRangeAsync(string blobName, long position, int length, string? leaseId, byte[] destination) {
        using var response = await HttpRetry.SendAsync(_http, () => build(downloadRequest(blobName, position, length, leaseId)));
        if (!response.IsSuccessStatusCode) throw error(response, "download");
        using var stream = await response.Content.ReadAsStreamAsync();
        await stream.ReadExactlyAsync(destination, 0, length);
    }
    public void DeleteBlobIfExists(string blobName) {
        var r = new Request { Method = HttpMethod.Delete, BlobName = blobName };
        using var response = HttpRetry.Send(_http, () => build(r));
        if (response.IsSuccessStatusCode) return;
        var ex = error(response, "delete");
        if (ex.StatusCode == 404) return; // BlobNotFound or ContainerNotFound
        if (ex.ErrorCode == "LeaseIdMissing") { // left over lease from a crashed process, break it and retry once
            BreakLease(blobName);
            using var retry = HttpRetry.Send(_http, () => build(new Request { Method = HttpMethod.Delete, BlobName = blobName }));
            if (retry.IsSuccessStatusCode || (int)retry.StatusCode == 404) return;
            throw error(retry, "delete");
        }
        throw ex;
    }
    Request leaseRequest(string blobName, string action) => new() {
        Method = HttpMethod.Put,
        BlobName = blobName,
        Query = { new("comp", "lease") },
        XmsHeaders = { ["x-ms-lease-action"] = action }
    };
    public string AcquireLease(string blobName) {
        var r = leaseRequest(blobName, "acquire");
        r.XmsHeaders["x-ms-lease-duration"] = "-1"; // infinite
        using var response = HttpRetry.Send(_http, () => build(r));
        if (!response.IsSuccessStatusCode) {
            if ((int)response.StatusCode == 409) throw new AzureBlobRequestException(409, "LeaseAlreadyPresent", $"The blob {blobName} is locked by another process. ");
            throw error(response, "acquire lease");
        }
        response.Headers.TryGetValues("x-ms-lease-id", out var leaseIds);
        return leaseIds?.FirstOrDefault() ?? throw new Exception("The lease response had no x-ms-lease-id header. ");
    }
    public void ReleaseLease(string blobName, string leaseId) {
        var r = leaseRequest(blobName, "release");
        r.XmsHeaders["x-ms-lease-id"] = leaseId;
        using var response = HttpRetry.Send(_http, () => build(r));
        if (!response.IsSuccessStatusCode) throw error(response, "release lease");
    }
    public void BreakLease(string blobName) {
        var r = leaseRequest(blobName, "break");
        r.XmsHeaders["x-ms-lease-break-period"] = "0";
        using var response = HttpRetry.Send(_http, () => build(r));
        if (!response.IsSuccessStatusCode) throw error(response, "break lease");
    }
    public void Dispose() {
        _http.Dispose();
    }
}
