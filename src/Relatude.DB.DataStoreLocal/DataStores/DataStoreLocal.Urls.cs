using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Files;
using Relatude.DB.FileConversion;
using Relatude.DB.Web;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    Guid _startUpGuid = Guid.NewGuid();
    string getFileVersionId(FileValue fileValue) {
        // return (fileValue.Hash + _startUpGuid).GenerateHashUInt().ToString();
        return fileValue.Hash.GetShortHashForUrl(_startUpGuid);
    }

    public bool TryParseUrl(string url, out UrlType type, [MaybeNullWhen(false)] out UrlParseResult? result, QueryContext? ctx = null) {
        result = null;
        ctx = ctx ?? _defaultQueryCtx;
        if (!_urlProviderPublic.TryParseUrlType(url, out type)) return false;
        switch (type) {
            case UrlType.LocalNode: {
                    if (_urlProviderPublic.TryParseUrlNodeKey(url, out var nodeKey)) {
                        result = new() {
                            UrlType = type,
                            NodeKey = nodeKey
                        };
                    }
                }
                break;
            case UrlType.LocalEmbeddedNode: {
                    if (_urlProviderPublic.TryParseUrlNodePath(url, out var nodePath)) {
                        result = new() {
                            UrlType = type,
                            NodeKey = nodePath.NodeKey,
                            NodePath = nodePath
                        };
                    }
                }
                break;
            case UrlType.LocalProperty: {
                    if (_urlProviderPublic.TryParseUrlPropertyPath(url, out var propertyPath)) {
                        result = new() {
                            UrlType = type,
                            NodeKey = propertyPath.NodePath.NodeKey,
                            NodePath = propertyPath.NodePath,
                            PropertyPath = propertyPath
                        };
                    }
                }
                break;
            case UrlType.LocalAdjusted: {
                    if (_urlProviderPublic.TryParseUrlAdjustments(url, out var propertyPath, out var adjustment)) {
                        result = new() {
                            UrlType = type,
                            NodeKey = propertyPath.NodePath.NodeKey,
                            NodePath = propertyPath.NodePath,
                            PropertyPath = propertyPath,
                            Adjustment = adjustment
                        };
                    }
                }
                break;
            case UrlType.LocalUrl:
            case UrlType.RemoteUrl:
            case UrlType.Email:
            default: break;
        }
        return result != null;
    }
    public bool TryParseUrlForContent(string url, out UrlType type, [MaybeNullWhen(false)] out UrlParseResultContent? r, int maxWaitMs = -1, QueryContext? ctx = null) {
        r = default;
        ctx = ctx ?? _defaultQueryCtx;
        bool v = TryParseUrl(url, out type, out UrlParseResult? result);
        if (!v || result == null) return false;
        r = new UrlParseResultContent(result);
        switch (result.UrlType) {
            case UrlType.LocalNode: {
                    r.NodeData = Get(result.NodeKey, _defaultQueryCtx);
                }
                break;
            case UrlType.LocalEmbeddedNode: {
                    r.NodeData = Get(result.NodePath!, _defaultQueryCtx);
                }
                break;
            case UrlType.LocalProperty: {
                    r.PropertyValue = GetValue<object>(result.PropertyPath!, _defaultQueryCtx);
                    if (r.PropertyValue is FileValue fileValue) {
                        r.FileValue = fileValue;
                        r.Stream = GetFileStream(result.PropertyPath!, _defaultQueryCtx).Result;
                        r.ContentType = fileValue.ContentType;
                        r.UsedFileName = fileValue.Name;
                    }
                }
                break;
            case UrlType.LocalAdjusted: {
                    r.PropertyValue = GetValue<object>(result.PropertyPath!, _defaultQueryCtx);
                    if (r.PropertyValue is FileValue fileValue) {
                        r.FileValue = fileValue;
                        var stateAndStream = GetFileStreamAndState(result.PropertyPath!, result.Adjustment!, maxWaitMs).Result;
                        r.Stream = stateAndStream.Stream;
                        r.Cacheable = stateAndStream.IsReady;
                        r.ContentType = FileFormatUtil.GetContentType(stateAndStream.RequestedFormat);
                        r.UsedFileName = Path.GetFileNameWithoutExtension(fileValue.Name) + FileFormatUtil.GetExtensionWithDot(stateAndStream.RequestedFormat);
                    }
                }
                break;
            case UrlType.LocalUrl:
            case UrlType.RemoteUrl:
            case UrlType.Email:
            default: throw new InvalidOperationException("Unexpected UrlType in this context: " + result.UrlType);
        }
        return r != null;
    }

    public string GetUrl(NodeKey nodeKey, bool absolute, QueryContext? ctx = null) {
        return _urlProviderPublic.GetUrl(nodeKey, absolute);
    }
    public string GetUrl(NodePath nodePath, bool absolute, QueryContext? ctx = null) {
        return _urlProviderPublic.GetUrl(nodePath, absolute);
    }
    public string GetUrl(PropertyPath propertyPath, bool absolute, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        if (fileValue.IsEmpty || fileValue.PropertyPath == null) {
            throw new Exception($"Property at path {propertyPath} does not contain a file.");
        }
        return _urlProviderPublic.GetUrl(fileValue.PropertyPath, getFileVersionId(fileValue), absolute);
    }
    public string GetUrl(PropertyPath propertyPath, FileAdjustment adj, bool absolute, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        if (fileValue.IsEmpty || fileValue.PropertyPath == null) {
            throw new Exception($"Property at path {propertyPath} does not contain a file.");
        }
        return _urlProviderPublic.GetUrl(fileValue.PropertyPath, adj, getFileVersionId(fileValue), absolute);
    }

    public async Task<Stream> GetFileStream(string url, int maxWait = -1, QueryContext? ctx = null) {
        return (await GetFileStreamAndState(url, maxWait, ctx)).Stream;
    }
    public async Task<StateAndStream> GetFileStreamAndState(string url, int maxWait = -1, QueryContext? ctx = null) {
        if (!_urlProviderPublic.TryParseUrlType(url, out var type)) throw new Exception("URL is not a valid local URL");
        if (type == UrlType.LocalProperty) {
            if (!_urlProviderPublic.TryParseUrlPropertyPath(url, out var path)) throw new Exception("URL does not point to a file property");
            var fileValue = GetValue<FileValue>(path, ctx);
            var fileStore = getFileStore(fileValue.StorageId);
            var stream = await fileStore.GetFileStream(fileValue);
            return new StateAndStream(stream, true, fileValue, fileValue.Format, Guid.Empty, null);
        } else if (type == UrlType.LocalAdjusted) {
            if (!_urlProviderPublic.TryParseUrlAdjustments(url, out var path, out var adj)) throw new Exception("URL does not point to an adjusted file property");
            return await GetFileStreamAndState(path, adj, maxWait, ctx);
        }
        throw new Exception("URL does not point to a file property");
    }


    public bool TryGetAddress(int id, [MaybeNullWhen(false)] out string? address, QueryContext? ctx = null) {
        return _addresses.TryGetAddressAndTryMatchCulture(id, getBestCultureId(ctx), out address);
    }
    public bool TryGetAddress(Guid id, [MaybeNullWhen(false)] out string? address, QueryContext? ctx = null) {
        if (_guids.TryGetId(id, out var uid)) {
            return TryGetAddress(uid, out address, ctx);
        }
        address = null;
        return false;
    }
    public bool TryGetAddress(NodeKey id, [MaybeNullWhen(false)] out string? meta, QueryContext? ctx = null) {
        if (id.Int == 0) return TryGetAddress(id.Guid, out meta, ctx);
        return TryGetAddress(id.Int, out meta, ctx);
    }

    public bool TryGetNodeIdFromAddress(string address, out Guid nodeId) {
        if (TryGetNodeIdFromAddress(address, out int uid)) {
            if (_guids.TryGetId(uid, out nodeId)) {
                return true;
            }
        }
        nodeId = Guid.Empty;
        return false;
    }
    public bool TryGetNodeIdFromAddress(string address, out Guid nodeId, out string? cultureCode) {
        if (TryGetNodeIdFromAddress(address, out int uid, out cultureCode)) {
            if (_guids.TryGetId(uid, out nodeId)) {
                return true;
            }
        }
        nodeId = Guid.Empty;
        cultureCode = null;
        return false;
    }
    public bool TryGetNodeIdFromAddress(string address, out int nodeId) {
        return _addresses.TryGetId(address, out nodeId, out _);
    }
    public bool TryGetNodeIdFromAddress(string address, out int nodeId, out string? cultureCode) {
        if (address == null) address = string.Empty; // treat null and empty as the same address
        if (_addresses.TryGetId(address, out nodeId, out var cultureId)) {
            _nativeModelStore.TryGetCultureCode(cultureId, out cultureCode);
            return true;
        }
        cultureCode = null;
        return false;
    }
    public bool TryGetNodeDataFromAddress(string address, [MaybeNullWhen(false)] out INodeDataExternal nodeData) {
        if (TryGetNodeIdFromAddress(address, out int uid, out string? cultureCode)) {
            return TryGet(uid, out nodeData, _defaultQueryCtx.Culture(cultureCode));
        }
        nodeData = null;
        return false;
    }


}
