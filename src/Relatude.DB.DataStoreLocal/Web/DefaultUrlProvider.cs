using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores;
using Relatude.DB.FileConversion;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Relatude.DB.Web;

public enum UrlFormat {
    AddressOrGuidId,
    AddressOrIntId,
    AddressAndIntId,
    AddressAndGuidId,
    IntIdOnly,
    GuidIdOnly,
}
public class UrlProviderOptions {
    public string? UrlNodeRoot { get; set; }
    public bool IncludeTrailingSlash { get; set; }
    public UrlFormat UrlFormat { get; set; }
    public Guid HashKey { get; set; }
    public bool HashPropertyUrls { get; set; }
    public bool UnCompressed { get; set; }
}

enum UrlDataType : byte {
    NodeKey,
    NodePath,
    PropertyPath,
    PropertyPathAdjusted,
}
class UrlData {
    public UrlDataType UrlType;
    public NodeKey? Key;
    public NodePath? Path;
    public PropertyPath? Property;
    public FileAdjustment? Adjustment;
    public string? ContentVersionId;
    static CompressionLevel compressionLevel = CompressionLevel.Fastest;
    char urlTypeChar(UrlDataType type) {
        return type switch {
            UrlDataType.NodeKey => 'k',
            UrlDataType.NodePath => 'n',
            UrlDataType.PropertyPath => 'p',
            UrlDataType.PropertyPathAdjusted => 'a',
            _ => throw new NotImplementedException(),
        };
    }
    static bool tryGetUrlType(char c, [MaybeNullWhen(false)] out UrlDataType type) {
        switch (c) {
            case 'k': type = UrlDataType.NodeKey; return true;
            case 'n': type = UrlDataType.NodePath; return true;
            case 'p': type = UrlDataType.PropertyPath; return true;
            case 'a': type = UrlDataType.PropertyPathAdjusted; return true;
            default: type = default; return false;
        }
    }
    public string? GetQueryParamValue(UrlProviderOptions options) {
        var ms = new MemoryStream();
        switch (UrlType) {
            case UrlDataType.NodeKey:
                // not needed as addressBase contains this info
                break;
            case UrlDataType.NodePath: {
                    var p = Path ?? throw new ArgumentNullException(nameof(Path));
                    ms.WriteByteArray(p.ToBytesWithoutNodeKey());
                }
                break;
            case UrlDataType.PropertyPath: {
                    var p = Property ?? throw new ArgumentNullException(nameof(Property));
                    var c = ContentVersionId ?? string.Empty;
                    ms.WriteByteArray(p.ToBytesWithoutNodeKey());
                    ms.WriteString(c);
                }
                break;
            case UrlDataType.PropertyPathAdjusted: {
                    var p = Property ?? throw new ArgumentNullException(nameof(Property));
                    var a = Adjustment ?? throw new ArgumentNullException(nameof(Adjustment));
                    var c = ContentVersionId ?? string.Empty;
                    ms.WriteByteArray(p.ToBytesWithoutNodeKey());
                    ms.WriteByteArray(a.ToBytes());
                    ms.WriteString(c);
                }
                break;
            default:
                throw new NotImplementedException();
        }
        if (options.HashPropertyUrls) {
            using var hmac = new HMACSHA256(options.HashKey.ToByteArray());
            var bytes = ms.ToArray();
            var hash = hmac.ComputeHash(bytes);
            ms = new MemoryStream();
            ms.Write(hash);
            ms.Write(bytes);
        }
        if (!options.UnCompressed) {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, compressionLevel)) brotli.Write(ms.ToArray());
            ms = new MemoryStream(output.ToArray());
        }
        if (ms.Length == 0) return null;
        return urlTypeChar(UrlType) + B64.EncodeForUrl(ms.ToArray());
    }

    public static UrlData ParseQueryParamValue(string? value, NodeKey key, UrlProviderOptions options) {
        var urlType = UrlDataType.NodeKey;
        if (value == null || value.Length < 2) return new UrlData() { UrlType = urlType, Key = key };
        if (!tryGetUrlType(value[0], out urlType)) throw new Exception($"Invalid URL type character: {value[0]}");
        if (!B64.TryDecodeFromUrlParameter(value[1..], out var bytes)) throw new Exception($"Invalid URL parameter: {value[1..]}");
        var ms = new MemoryStream(bytes);

        if (!options.UnCompressed) {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(ms, CompressionMode.Decompress)) brotli.CopyTo(output);
            ms = new MemoryStream(output.ToArray());
        }
        if (options.HashPropertyUrls) {
            var all = ms.ToArray();
            if (all.Length < 32) throw new Exception("Invalid URL: hash prefix missing.");
            using var hmac = new HMACSHA256(options.HashKey.ToByteArray());
            if (!hmac.ComputeHash(all[32..]).AsSpan().SequenceEqual(all.AsSpan(0, 32)))
                throw new Exception("Invalid URL: hash mismatch.");
            ms = new MemoryStream(all[32..]);
        }
        switch (urlType) {
            case UrlDataType.NodeKey:
                return new UrlData() {
                    UrlType = urlType,
                    Key = key
                };
            case UrlDataType.NodePath:
                if (ms == null) throw new Exception("Missing data for NodePath URL type.");
                return new UrlData() {
                    UrlType = urlType,
                    Path = NodePath.FromBytesWithGivenNodeKey(key, ms.ReadByteArray())
                };
            case UrlDataType.PropertyPath:
                if (ms == null) throw new Exception("Missing data for PropertyPath URL type.");
                return new UrlData() {
                    UrlType = urlType,
                    Property = PropertyPath.FromBytesWithGivenNodeKey(key, ms.ReadByteArray()),
                    ContentVersionId = ms.ReadString()
                };
            case UrlDataType.PropertyPathAdjusted:
                if (ms == null) throw new Exception("Missing data for PropertyPathAdjusted URL type.");
                return new UrlData() {
                    UrlType = urlType,
                    Property = PropertyPath.FromBytesWithGivenNodeKey(key, ms.ReadByteArray()),
                    Adjustment = FileAdjustment.FromBytes(ms.ReadByteArray()),
                    ContentVersionId = ms.ReadString()
                };
            default:
                throw new NotImplementedException();
        }
    }
    public static bool TryParseUrlType(string? value, out UrlDataType type) {
        if (value == null || value.Length < 1) {
            type = UrlDataType.NodeKey;
            return true;
        }
        return tryGetUrlType(value[0], out type);
    }
}

public class DefaultUrlProvider : IUrlProvider {
    const char slash = '/';
    readonly UrlProviderOptions _options;
    readonly string _urlNodeRoot;
    IDataStore? _dataStore;
    public DefaultUrlProvider(UrlProviderOptions? options) {
        _options = options ?? new UrlProviderOptions();
        _urlNodeRoot = enforceTrailingSlash(_options.UrlNodeRoot);
    }
    protected IDataStore DataStore => _dataStore ?? throw new InvalidOperationException("Data store has not been initialized. Call Initialize() before using this method.");
    string enforceTrailingSlash(string? urlRoot) {
        if (string.IsNullOrWhiteSpace(urlRoot)) urlRoot = string.Empty;
        if (!urlRoot.EndsWith("/")) urlRoot += "/";
        if (!urlRoot.StartsWith("/")) urlRoot = "/" + urlRoot;
        return urlRoot;
    }
    public void Initialize(IDataStore dataStore) => _dataStore = dataStore;
    int getId(NodeKey key) => key.HasInt ? key.Int : DataStore.GetId(key.Guid);
    Guid getGuid(NodeKey key) => key.HasGuid ? key.Guid : DataStore.GetGuid(key.Int);
    string urlParamSafeName(string name) {
        // max length of 20
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new StringBuilder();
        foreach (var c in name) {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') {
                sb.Append(c);
            } else {
                sb.Append('_');
            }
            if (sb.Length >= 20) break;
        }
        return sb.ToString();
    }
    readonly string _queryParamName = "nid";
    string getBaseUrl(NodeKey idKey, bool absolute) {
        if (absolute) throw new NotImplementedException("Absolute URLs are not implemented yet.");
        var url = _options.UrlFormat switch {
            UrlFormat.AddressOrGuidId => DataStore.TryGetAddress(idKey, out var address) ? address : getGuid(idKey).ToString(),
            UrlFormat.AddressOrIntId => DataStore.TryGetAddress(idKey, out var address) ? address : getId(idKey).ToString(),
            UrlFormat.AddressAndIntId => DataStore.TryGetAddress(idKey, out var address) ? $"{getId(idKey)}{slash}{address}" : getId(idKey).ToString(),
            UrlFormat.AddressAndGuidId => DataStore.TryGetAddress(idKey, out var address) ? $"{getGuid(idKey)}{slash}{address}" : getGuid(idKey).ToString(),
            UrlFormat.IntIdOnly => getId(idKey).ToString(),
            UrlFormat.GuidIdOnly => getGuid(idKey).ToString(),
            _ => throw new NotImplementedException(),
        };
        if (url == null) url = string.Empty;
        if (_options.IncludeTrailingSlash && !url.EndsWith("/")) url += "/";
        return _urlNodeRoot + url;
    }
    string getBasePropertyUrl(PropertyPath property, bool absolute, string? fileExtWithDot) {
        var baseUrl = getBaseUrl(property.NodePath.NodeKey, absolute);
        if (!baseUrl.EndsWith(slash)) baseUrl += slash;
        if (DataStore.TryGetValue<FileValue>(property, out var value) && !value.IsEmpty) {
            if (!baseUrl.EndsWith(slash)) baseUrl += slash;
            if (fileExtWithDot == null) {
                baseUrl += urlParamSafeName(Path.GetFileNameWithoutExtension(value.Name)) + urlParamSafeName(Path.GetExtension(value.Name));
            } else {
                baseUrl += urlParamSafeName(Path.GetFileNameWithoutExtension(value.Name)) + urlParamSafeName(fileExtWithDot);
            }
        }
        return baseUrl;
    }
    public string GetUrl(NodeKey idKey, bool absolute) {
        return getBaseUrl(idKey, absolute);
    }
    public string GetUrl(NodePath nodePath, bool absolute) {
        if (nodePath.Path.Length == 0) return GetUrl(nodePath.NodeKey, absolute);
        var addressBase = getBaseUrl(nodePath.NodeKey, absolute);
        var urlData = new UrlData() {
            UrlType = UrlDataType.NodePath,
            Path = nodePath
        };
        var queryParam = urlData.GetQueryParamValue(_options);
        return queryParam == null ? addressBase : $"{addressBase}?{_queryParamName}={queryParam}";
    }
    public string GetUrl(PropertyPath property, string? contentVersionId, bool absolute) {
        var addressBase = getBasePropertyUrl(property, absolute, null);
        var urlData = new UrlData() {
            UrlType = UrlDataType.PropertyPath,
            Property = property,
            ContentVersionId = contentVersionId
        };
        var queryParam = urlData.GetQueryParamValue(_options);
        return $"{addressBase}?{_queryParamName}={queryParam}";
    }
    public string GetUrl(PropertyPath property, FileAdjustment adjustment, string? contentVersionId, bool absolute) {
        var addressBase = getBasePropertyUrl(property, absolute, FileFormatUtil.GetExtensionWithDot(adjustment.RequestedFormat));
        var urlData = new UrlData() {
            UrlType = UrlDataType.PropertyPathAdjusted,
            Property = property,
            Adjustment = adjustment,
            ContentVersionId =contentVersionId
        };
        var queryParam = urlData.GetQueryParamValue(_options);
        return $"{addressBase}?{_queryParamName}={queryParam}";
    }

    public bool TryParseUrlType(string localUrl, out UrlType type) {
        if (_urlNodeRoot.Length >= localUrl.Length || !localUrl.StartsWith(_urlNodeRoot)) {
            type = UrlType.RemoteUrl;
            return false;
        }
        parseQueryParamValue(localUrl, out var queryParamValue, out _);
        if (!UrlData.TryParseUrlType(queryParamValue, out var urlDataType)) {
            type = default;
            return false;
        }
        type = urlDataType switch {
            UrlDataType.NodeKey => UrlType.LocalNode,
            UrlDataType.NodePath => UrlType.LocalEmbeddedNode,
            UrlDataType.PropertyPath => UrlType.LocalProperty,
            UrlDataType.PropertyPathAdjusted => UrlType.LocalAdjusted,
            _ => throw new NotImplementedException(),
        };
        return true;
    }
    public bool TryParseUrlNodeKey(string url, out NodeKey nodeKey) => TryParseUrlNodeKey(url, out nodeKey, out _);
    void parseQueryParamValue(string url, out string? value, out int posQuery) {
        int startPos = _urlNodeRoot.Length;
        posQuery = url.IndexOf('?', startPos);
        if (posQuery == -1) {
            posQuery = url.Length;
            value = null;
        } else {
            var search = $"{_queryParamName}=";
            var from = url.IndexOf(search, posQuery);
            if (from == -1) {
                value = null;
            } else {
                from += search.Length;
                var to = url.IndexOf(search, from);
                if (to == -1) to = url.Length;
                value = url[from..to];
            }
        }

    }
    bool TryParseUrlNodeKey(string url, out NodeKey nodeKey, out string? queryParamValue) {

        if (_urlNodeRoot.Length >= url.Length || !url.StartsWith(_urlNodeRoot)) {
            // url does not start with the expected root, cannot parse node key
            nodeKey = default;
            queryParamValue = null;
            return false;
        }

        // First try, using full address part of url:
        parseQueryParamValue(url, out queryParamValue, out var posQuery);
        var startPos = _urlNodeRoot.Length;
        var addressWithFileName = url.Substring(startPos, posQuery - startPos);
        if (DataStore.TryGetNodeIdFromAddress(addressWithFileName, out int nodeId, out var cultureCode)) {
            nodeKey = new NodeKey(nodeId);
            return true;
        }

        // Second try, lookup using shorter address to exclude filename that could be added to the end of the url:
        var posLastSlash = url.LastIndexOf(slash);
        if (posLastSlash < 1) posLastSlash = posQuery;
        var addressWithoutFileName = posLastSlash < posQuery ? url[startPos..posLastSlash] : addressWithFileName;
        if (addressWithFileName.Length != addressWithoutFileName.Length) {
            if (DataStore.TryGetNodeIdFromAddress(addressWithoutFileName, out nodeId, out cultureCode)) {
                nodeKey = new NodeKey(nodeId);
                return true;
            }
        }

        // Third try, parse the id from the address part of the url:
        var posFirstSlash = addressWithoutFileName.IndexOf(slash, 1);
        if (posFirstSlash == -1) posFirstSlash = addressWithoutFileName.Length;
        var idString = addressWithoutFileName.AsSpan(0, posFirstSlash);
        switch (_options.UrlFormat) {
            case UrlFormat.GuidIdOnly:
            case UrlFormat.AddressOrGuidId:
            case UrlFormat.AddressAndGuidId: {
                    if (Guid.TryParse(idString, out var guid)) {
                        if (DataStore.Exists(guid)) {
                            nodeKey = new NodeKey(guid);
                            return true;
                        }
                    }
                }
                break;
            case UrlFormat.IntIdOnly:
            case UrlFormat.AddressOrIntId:
            case UrlFormat.AddressAndIntId: {
                    if (int.TryParse(idString, out var id)) {
                        if (DataStore.Exists(id)) {
                            nodeKey = new NodeKey(id);
                            return true;
                        }
                    }
                }
                break;
            default: throw new NotImplementedException($"UrlFormat {_options.UrlFormat} is not implemented.");
        }
        nodeKey = default;
        return false;
    }
    public bool TryParseUrlNodePath(string localUrl, [MaybeNullWhen(false)] out NodePath nodePath) {
        if (!TryParseUrlNodeKey(localUrl, out var nodeKey, out var queryParam)) {
            nodePath = default;
            return false;
        }
        var d = UrlData.ParseQueryParamValue(queryParam, nodeKey, _options);
        if (d.Path == null || d.UrlType != UrlDataType.NodePath) {
            nodePath = default;
            return false;
        }
        nodePath = d.Path;
        return true;
    }
    public bool TryParseUrlPropertyPath(string localUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath) {
        if (!TryParseUrlNodeKey(localUrl, out var nodeKey, out var queryParam)) {
            propertyPath = default;
            return false;
        }
        var d = UrlData.ParseQueryParamValue(queryParam, nodeKey, _options);
        if (d.Property == null || d.UrlType != UrlDataType.PropertyPath) {
            propertyPath = default;
            return false;
        }
        propertyPath = d.Property;
        return true;
    }
    public bool TryParseUrlAdjustments(string localUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath, [MaybeNullWhen(false)] out FileAdjustment adjustment) {
        if (!TryParseUrlNodeKey(localUrl, out var nodeKey, out var queryParam)) {
            propertyPath = default;
            adjustment = default;
            return false;
        }
        var d = UrlData.ParseQueryParamValue(queryParam, nodeKey, _options);
        if (d.Property == null || d.Adjustment == null || d.UrlType != UrlDataType.PropertyPathAdjusted) {
            propertyPath = default;
            adjustment = default;
            return false;
        }
        propertyPath = d.Property;
        adjustment = d.Adjustment;
        return true;
    }

}
