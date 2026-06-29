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
    public Guid HashKey { get; set; } = Guid.Empty;
    public string UrlNodeRoot { get; set; } = string.Empty;
    public bool IncludeTrailingSlash { get; set; } = false;
    public UrlFormat UrlFormat { get; set; } = UrlFormat.AddressOrGuidId;
    public bool HashNodeUrls { get; set; } = false;
    public bool HashPropertyUrls { get; set; } = false;
    public Guid UrlHashSeed { get; set; }
}

enum UrlDataType : byte {
    NodeKey,
    NodePath,
    PropertyPath,
    PropertyPathAdjusted,
}
class UrlData {
    const string UrlAddressPrefix = "nid=";
    public string? AddressBase;
    public UrlDataType UrlType;
    public NodeKey? Key;
    public NodePath? Path;
    public PropertyPath? Property;
    public FileAdjustment? Adjustment;
    const bool UseCompression = true;
    const int SignatureBytes = 8;
    static CompressionLevel compressionLevel = CompressionLevel.SmallestSize;
    string serializeQuery() {
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
                    ms.WriteByteArray(p.ToBytesWithoutNodeKey());
                }
                break;
            case UrlDataType.PropertyPathAdjusted: {
                    var p = Property ?? throw new ArgumentNullException(nameof(Property));
                    ms.WriteByteArray(p.ToBytesWithoutNodeKey());
                    var a = Adjustment ?? throw new ArgumentNullException(nameof(Adjustment));
                    ms.WriteByteArray(a.ToBytes());
                }
                break;
            default:
                throw new NotImplementedException();
        }
        if (UseCompression) {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, compressionLevel)) brotli.Write(ms.ToArray());
            ms = new MemoryStream(output.ToArray());
        }
        return urlTypeChar(UrlType) + B64.EncodeForUrl(ms.ToArray());
    }
    public string ToUrl() {
        return AddressBase + "?" + UrlAddressPrefix + serializeQuery();
    }
    public static UrlData ParseFromUrl(string url, NodeKey key) {
        var startQuery = url.IndexOf('?');
        byte[]? bytes = null;
        var urlType = UrlDataType.NodeKey;
        string addressBase;
        if (startQuery > -1) {
            addressBase = url[..startQuery];
            var query = url[(startQuery + 1)..];
            var startOfValue = query.IndexOf(UrlAddressPrefix);
            if (startOfValue > -1) {
                startOfValue += UrlAddressPrefix.Length;
                var endOfValue = query.IndexOf('&', startOfValue);
                if (endOfValue < 0) endOfValue = query.Length;
                query = query[startOfValue..endOfValue];
                if (query != null && query.Length > 2) {
                    if (!tryGetUrlType(query[0], out urlType)) throw new Exception($"Invalid URL type character: {query[0]}");
                    if (!B64.TryDecodeFromUrlParameter(query[1..], out bytes)) throw new Exception($"Invalid URL parameter: {query[1..]}");
                }
            }
        } else {
            addressBase = url;
        }
        MemoryStream? ms = null;
        if (bytes != null) {
            if (UseCompression) {
                using var input = new MemoryStream(bytes);
                using var output = new MemoryStream();
                using (var brotli = new BrotliStream(input, CompressionMode.Decompress)) brotli.CopyTo(output);
                bytes = output.ToArray();
            }
            ms = new MemoryStream(bytes);
        }
        switch (urlType) {
            case UrlDataType.NodeKey:
                return new UrlData() {
                    AddressBase = addressBase,
                    UrlType = urlType,
                    Key = key
                };
            case UrlDataType.NodePath:
                if (ms == null) throw new Exception("Missing data for NodePath URL type.");
                return new UrlData() {
                    AddressBase = addressBase,
                    UrlType = urlType,
                    Path = NodePath.FromBytesWithGivenNodeKey(key, ms.ReadByteArray())
                };
            case UrlDataType.PropertyPath:
                if (ms == null) throw new Exception("Missing data for PropertyPath URL type.");
                return new UrlData() {
                    AddressBase = addressBase,
                    UrlType = urlType,
                    Property = PropertyPath.FromBytesWithGivenNodeKey(key, ms.ReadByteArray())
                };
            case UrlDataType.PropertyPathAdjusted:
                if (ms == null) throw new Exception("Missing data for PropertyPathAdjusted URL type.");
                return new UrlData() {
                    AddressBase = addressBase,
                    UrlType = urlType,
                    Property = PropertyPath.FromBytesWithGivenNodeKey(key, ms.ReadByteArray()),
                    Adjustment = FileAdjustment.FromBytes(ms.ReadByteArray())
                };
            default:
                throw new NotImplementedException();
        }
    }
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
    public static bool TryParseTypeFromUrl(string url, out UrlDataType type) {
        string? query = null;
        var startQuery = url.IndexOf('?');
        if (startQuery > -1) {
            query = url[(startQuery + 1)..];
            var startOfValue = query.IndexOf(UrlAddressPrefix);
            if (startOfValue > -1) {
                startOfValue += UrlAddressPrefix.Length;
                var endOfValue = query.IndexOf('&', startOfValue);
                if (endOfValue < 0) endOfValue = query.Length;
                query = query[startOfValue..endOfValue];
            }
        }
        if (string.IsNullOrWhiteSpace(query)) {
            type = UrlDataType.NodeKey;
            return true;
        }
        return tryGetUrlType(query[0], out type);
    }
}

public class DefaultUrlProvider : IUrlProvider {
    const char slash = '/';
    readonly IUrlFileAdjustmentEncoder _encoder;
    readonly UrlProviderOptions _options;
    readonly string _urlNodeRoot;
    IDataStore? _dataStore;
    public DefaultUrlProvider(UrlProviderOptions? options) {
        _options = options ?? new UrlProviderOptions();
        _encoder = new BinaryUrlFileAdjustmentEncoder(_options.HashKey);
        _urlNodeRoot = enforceTrailingSlash(_options.UrlNodeRoot);
    }
    protected IDataStore DataStore => _dataStore ?? throw new InvalidOperationException("Data store has not been initialized. Call Initialize() before using this method.");
    string enforceTrailingSlash(string urlRoot) {
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
    public bool IsUrlRelevant(string url) {
        if (url.StartsWith(_urlNodeRoot)) return true;
        return false;
    }
    public string GetUrl(NodeKey idKey, bool absolute) {
        return getBaseUrl(idKey, absolute);
    }
    public string GetUrl(NodePath nodePath, bool absolute) {
        if (nodePath.Path.Length == 0) return GetUrl(nodePath.NodeKey, absolute);
        return new UrlData() {
            AddressBase = getBaseUrl(nodePath.NodeKey, absolute),
            UrlType = UrlDataType.NodePath,
            Path = nodePath
        }.ToUrl();
    }
    public string GetUrl(PropertyPath property, string? contentVersionId, bool absolute) {
        return new UrlData() {
            AddressBase = getBasePropertyUrl(property, absolute, null),
            UrlType = UrlDataType.PropertyPath,
            Property = property
        }.ToUrl();
    }
    public string GetUrl(PropertyPath property, FileAdjustment adjustment, string? contentVersionId, bool absolute) {
        return new UrlData() {
            AddressBase = getBasePropertyUrl(property, absolute, FileFormatUtil.GetExtensionWithDot(adjustment.RequestedFormat)),
            UrlType = UrlDataType.PropertyPathAdjusted,
            Property = property,
            Adjustment = adjustment
        }.ToUrl();
    }
    public bool TryParseUrlType(string localUrl, out UrlType type) {
        if (!UrlData.TryParseTypeFromUrl(localUrl, out var urlDataType)) {
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
    public bool TryParseUrlNodeKey(string url, out NodeKey nodeKey) {

        if (_urlNodeRoot.Length >= url.Length || !url.StartsWith(_urlNodeRoot)) {
            // url does not start with the expected root, cannot parse node key
            nodeKey = default;
            return false;
        }

        // First try, using full address part of url:
        var startPos = _urlNodeRoot.Length;
        var posQuery = url.IndexOf('?', startPos);
        if (posQuery == -1) posQuery = url.Length;
        var addressWithFileName = url.Substring(startPos, posQuery - startPos);
        if (DataStore.TryGetNodeIdFromAddress(addressWithFileName, out int nodeId, out var cultureCode)) {
            nodeKey = new NodeKey(nodeId);
            return true;
        }

        // Second try, lookup using shorter address to exclude filename that could be added to the end of the url:
        var posLastSlash = url.LastIndexOf(slash);
        if (posLastSlash == -1) posLastSlash = posQuery;
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
                        nodeKey = new NodeKey(guid);
                        return true;
                    }
                }
                break;
            case UrlFormat.IntIdOnly:
            case UrlFormat.AddressOrIntId:
            case UrlFormat.AddressAndIntId: {
                    if (int.TryParse(idString, out var id)) {
                        nodeKey = new NodeKey(id);
                        return true;
                    }
                }
                break;
            default: throw new NotSupportedException();
        }

        throw new Exception($"Unable to parse node key from URL: {url}");
    }
    public bool TryParseUrlNodePath(string localUrl, [MaybeNullWhen(false)] out NodePath nodePath) {
        if (!TryParseUrlNodeKey(localUrl, out var nodeKey)) {
            nodePath = default;
            return false;
        }
        var d = UrlData.ParseFromUrl(localUrl, nodeKey);
        if (d.Path == null || d.UrlType != UrlDataType.NodePath) {
            nodePath = default;
            return false;
        }
        nodePath = d.Path;
        return true;
    }
    public bool TryParseUrlPropertyPath(string localUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath) {
        if (!TryParseUrlNodeKey(localUrl, out var nodeKey)) {
            propertyPath = default;
            return false;
        }
        var d = UrlData.ParseFromUrl(localUrl, nodeKey);
        if (d.Property == null || d.UrlType != UrlDataType.PropertyPath) {
            propertyPath = default;
            return false;
        }
        propertyPath = d.Property;
        return true;
    }
    public bool TryParseUrlAdjustments(string localUrl, [MaybeNullWhen(false)] out PropertyPath propertyPath, [MaybeNullWhen(false)] out FileAdjustment adjustment) {
        if (!TryParseUrlNodeKey(localUrl, out var nodeKey)) {
            propertyPath = default;
            adjustment = default;
            return false;
        }
        var d = UrlData.ParseFromUrl(localUrl, nodeKey);
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
