using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Files;
using Relatude.DB.FileConversion;
using Relatude.DB.IO;
using Relatude.DB.Web;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    Guid _startUpGuid = Guid.NewGuid();
    string getFileVersionId(FileValue fileValue) {
        // return (fileValue.Hash + _startUpGuid).GenerateHashUInt().ToString();
        return fileValue.Hash.GetShortHashForUrl(_startUpGuid);
    }

    public bool TryParseUrl(string url, [MaybeNullWhen(false)] out UrlKeys result, QueryContext? ctx = null) {
        result = null;
        ctx = ctx ?? _defaultQueryCtx;
        if (!_urlProviderPublic.TryParseUrlTarget(url, out var type)) return false;

        switch (type) {
            case UrlTarget.Node: {
                    if (_urlProviderPublic.TryParseUrlNodeKey(url, out var nodeKey)) {
                        result = new() {
                            Target = type,
                            NodeKey = nodeKey
                        };
                    }
                }
                break;
            case UrlTarget.EmbeddedNode: {
                    if (_urlProviderPublic.TryParseUrlNodePath(url, out var nodePath)) {
                        result = new() {
                            Target = type,
                            NodeKey = nodePath.NodeKey,
                            NodePath = nodePath
                        };
                    }
                }
                break;
            case UrlTarget.Property: {
                    if (_urlProviderPublic.TryParseUrlPropertyPath(url, out var propertyPath)) {
                        result = new() {
                            Target = type,
                            NodeKey = propertyPath.NodePath.NodeKey,
                            NodePath = propertyPath.NodePath,
                            PropertyPath = propertyPath
                        };
                    }
                }
                break;
            case UrlTarget.PropertyAdjusted: {
                    if (_urlProviderPublic.TryParseUrlAdjustments(url, out var propertyPath, out var adjustment)) {
                        result = new() {
                            Target = type,
                            NodeKey = propertyPath.NodePath.NodeKey,
                            NodePath = propertyPath.NodePath,
                            PropertyPath = propertyPath,
                            Adjustment = adjustment
                        };
                    }
                }
                break;
            default: break;
        }
        return result != null;
    }
    public bool TryParseUrlForContent(string url, [MaybeNullWhen(false)] out UrlContent r, int maxWaitMs = -1, QueryContext? ctx = null) {
        r = default;
        ctx = ctx ?? _defaultQueryCtx;
        bool v = TryParseUrl(url, out UrlKeys? result, ctx);
        if (!v || result == null) return false;
        r = new UrlContent(result);
        switch (result.Target) {
            case UrlTarget.Node: {
                    r.NodeData = Get(result.NodeKey, _defaultQueryCtx);
                }
                break;
            case UrlTarget.EmbeddedNode: {
                    r.NodeData = Get(result.NodePath!, _defaultQueryCtx);
                }
                break;
            case UrlTarget.Property: {
                    r.PropertyValue = GetValue<object>(result.PropertyPath!, _defaultQueryCtx);
                    if (r.PropertyValue is FileValue fileValue) {
                        r.FileValue = fileValue;
                        r.Stream = GetFileStream(result.PropertyPath!, _defaultQueryCtx).Result;
                        r.ContentType = fileValue.ContentType;
                        r.FileName = fileValue.Name;
                    }
                }
                break;
            case UrlTarget.PropertyAdjusted: {
                    r.PropertyValue = GetValue<object>(result.PropertyPath!, _defaultQueryCtx);
                    if (r.PropertyValue is FileValue fileValue) {
                        r.FileValue = fileValue;
                        var stateAndStream = GetFileStreamAndState(result.PropertyPath!, result.Adjustment!, maxWaitMs).Result;
                        r.Stream = stateAndStream.Stream;
                        r.Cacheable = stateAndStream.IsReady;
                        r.ContentType = FileFormatUtil.GetContentType(stateAndStream.RequestedFormat);
                        r.FileName = Path.GetFileNameWithoutExtension(fileValue.Name) + FileFormatUtil.GetExtensionWithDot(stateAndStream.RequestedFormat);
                    }
                }
                break;
            default: throw new InvalidOperationException("Unexpected UrlType in this context: " + result.Target);
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
        if (!_urlProviderPublic.TryParseUrlTarget(url, out var type)) throw new Exception("URL is not a valid local URL");
        if (type == UrlTarget.Property) {
            if (!_urlProviderPublic.TryParseUrlPropertyPath(url, out var path)) throw new Exception("URL does not point to a file property");
            var fileValue = GetValue<FileValue>(path, ctx);
            var fileStore = getFileStore(fileValue.StorageId);
            var stream = await fileStore.GetFileStream(fileValue);
            return new StateAndStream(stream, true, fileValue, fileValue.Format, Guid.Empty, null);
        } else if (type == UrlTarget.PropertyAdjusted) {
            if (!_urlProviderPublic.TryParseUrlAdjustments(url, out var path, out var adj)) throw new Exception("URL does not point to an adjusted file property");
            return await GetFileStreamAndState(path, adj, maxWait, ctx);
        }
        throw new Exception("URL does not point to a file property");
    }


    public bool TryGetAddress(int id, [MaybeNullWhen(false)] out string? address, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            return _addresses.TryGetAddressAndTryMatchCulture(id, getBestCultureId(ctx), out address);
        } finally {
            _lock.ExitReadLock();
        }
    }
    public bool TryGetAddress(Guid id, [MaybeNullWhen(false)] out string? address, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            if (_guids.TryGetId(id, out var uid)) {
                return TryGetAddress(uid, out address, ctx);
            }
            address = null;
            return false;
        } finally {
            _lock.ExitReadLock();
        }
    }
    public bool TryGetAddress(NodeKey id, [MaybeNullWhen(false)] out string? meta, QueryContext? ctx = null) {
        if (id.Int == 0) return TryGetAddress(id.Guid, out meta, ctx);
        return TryGetAddress(id.Int, out meta, ctx);
    }

    public bool TryGetNodeIdFromAddress(string address, out Guid nodeId) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            if (TryGetNodeIdFromAddress(address, out int uid)) {
                if (_guids.TryGetId(uid, out nodeId)) {
                    return true;
                }
            }
            nodeId = Guid.Empty;
            return false;
        } finally {
            _lock.ExitReadLock();
        }
    }
    public bool TryGetNodeIdFromAddress(string address, out Guid nodeId, out string? cultureCode) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            if (TryGetNodeIdFromAddress(address, out int uid, out cultureCode)) {
                if (_guids.TryGetId(uid, out nodeId)) {
                    return true;
                }
            }
            nodeId = Guid.Empty;
            cultureCode = null;
            return false;
        } finally {
            _lock.ExitReadLock();
        }
    }
    public bool TryGetNodeIdFromAddress(string address, out int nodeId) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            return _addresses.TryGetId(address, out nodeId, out _);
        } finally {
            _lock.ExitReadLock();
        }
    }
    public bool TryGetNodeIdFromAddress(string address, out int nodeId, out string? cultureCode) {
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            if (address == null) address = string.Empty; // treat null and empty as the same address
            if (_addresses.TryGetId(address, out nodeId, out var cultureId)) {
                _nativeModelStore.TryGetCultureCode(cultureId, out cultureCode);
                return true;
            }
            cultureCode = null;
            return false;
        } finally {
            _lock.ExitReadLock();
        }
    }
    public bool TryGetNodeDataFromAddress(string address, [MaybeNullWhen(false)] out INodeDataExternal nodeData) {
        if (TryGetNodeIdFromAddress(address, out int uid, out string? cultureCode)) {
            return TryGet(uid, out nodeData, _defaultQueryCtx.Culture(cultureCode));
        }
        nodeData = null;
        return false;
    }


}
