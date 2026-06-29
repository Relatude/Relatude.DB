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


    public bool IsUrlRelevant(string url) => _urlProviderPublic.IsUrlRelevant(url);
    public bool TryParseUrlType(string url, out UrlType type) => _urlProviderPublic.TryParseUrlType(url, out type);
    public bool TryParseUrlAdjustments(string url, [MaybeNullWhen(false)] out PropertyPath propertyPath, [MaybeNullWhen(false)] out FileAdjustment adjustment)
        => _urlProviderPublic.TryParseUrlAdjustments(url, out propertyPath, out adjustment);
    public bool TryParseUrlNodeKey(string url, [MaybeNullWhen(false)] out NodeKey nodeKey)
        => _urlProviderPublic.TryParseUrlNodeKey(url, out nodeKey);
    public bool TryParseUrlNodePath(string url, [MaybeNullWhen(false)] out NodePath nodePath)
        => _urlProviderPublic.TryParseUrlNodePath(url, out nodePath);
    public bool TryParseUrlPropertyPath(string url, [MaybeNullWhen(false)] out PropertyPath propertyPath)
        => _urlProviderPublic.TryParseUrlPropertyPath(url, out propertyPath);


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
            return new StateAndStream(stream, true, fileValue, fileValue.Format, Guid.Empty);
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
