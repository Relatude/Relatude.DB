using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;

namespace Relatude.DB.Web;

/// <summary>One host served by a <see cref="TreeUrlManager"/>, mapped to the root node of its tree.</summary>
public class UrlDomain {
    /// <summary>Host name, e.g. "www.domain1.no". Compared case-insensitively, without port.</summary>
    public string Host { get; set; } = string.Empty;
    /// <summary>The node whose subtree this host serves. The root itself is served at "/", and its address is not part of any URL.</summary>
    public Guid RootId { get; set; }
}

public class TreeUrlManagerOptions {
    /// <summary>The relation that connects a node to its parent. Alternatively set <see cref="ParentRelationName"/>.</summary>
    public Guid ParentRelationId { get; set; }
    /// <summary>CodeName or full name of the parent relation, resolved against the datamodel when the store initializes.</summary>
    public string? ParentRelationName { get; set; }
    /// <summary>True when the parent is on the source side of the relation (a node reaches its parent by following the relation from target to source).</summary>
    public bool ParentIsRelationSource { get; set; } = true;
    /// <summary>Host to root mappings. When empty, every node is routable and the top of the tree is part of the path.</summary>
    public List<UrlDomain> Domains { get; set; } = [];
    /// <summary>Root used for requests with an unknown or missing host (local development, staging). Defaults to the first configured domain's root.</summary>
    public Guid? FallbackRootId { get; set; }
    /// <summary>Scheme used for absolute URLs.</summary>
    public string Scheme { get; set; } = "https";
    /// <summary>Upper bound on the parent-chain walk, guarding against relation cycles.</summary>
    public int MaxDepth { get; set; } = 32;
}

/// <summary>
/// A url manager that builds URLs from the node's ancestor chain: the address of every node on the
/// path from (but not including) the domain root down to the node itself, joined by slashes.
/// Addresses only have to be unique among nodes that share the same parent-chain path, so
/// "tv/sony-x90/info" and "mobile/pixel/info" can both carry the plain address segment "info".
/// Domains map hosts to root nodes, so the same path can resolve to different nodes per host,
/// while nothing about the domain is ever stored on the nodes.
/// </summary>
public class TreeUrlManager : IUrlManager {
    readonly TreeUrlManagerOptions _o;
    IDataStore _db = default!;
    Guid _relationId;
    readonly Dictionary<string, Guid> _rootByHost = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<Guid, string> _hostByRoot = new();
    Guid _fallbackRootId;

    public TreeUrlManager(TreeUrlManagerOptions options) {
        _o = options;
    }
    public void Initialize(IDataStore store) {
        _db = store;
        _relationId = _o.ParentRelationId;
        if (_relationId == Guid.Empty) {
            if (string.IsNullOrEmpty(_o.ParentRelationName)) throw new Exception("TreeUrlManager requires ParentRelationId or ParentRelationName.");
            var relation = store.Datamodel.Relations.Values.FirstOrDefault(r =>
                string.Equals(r.FullName(), _o.ParentRelationName, StringComparison.Ordinal)
                || string.Equals(r.CodeName, _o.ParentRelationName, StringComparison.Ordinal));
            if (relation == null) throw new Exception("TreeUrlManager could not find the relation \"" + _o.ParentRelationName + "\" in the datamodel.");
            _relationId = relation.Id;
        }
        foreach (var domain in _o.Domains) {
            if (string.IsNullOrWhiteSpace(domain.Host) || domain.RootId == Guid.Empty) throw new Exception("TreeUrlManager domains require both Host and RootId.");
            _rootByHost[domain.Host.Trim()] = domain.RootId;
            if (!_hostByRoot.ContainsKey(domain.RootId)) _hostByRoot[domain.RootId] = domain.Host.Trim();
        }
        _fallbackRootId = _o.FallbackRootId ?? (_o.Domains.Count > 0 ? _o.Domains[0].RootId : Guid.Empty);
    }

    public string? TryGetUrl(NodeMeta meta, bool absolute) {
        if (!tryBuildPath(meta, out var path, out var rootId)) return null;
        if (!absolute) return path;
        if (rootId != Guid.Empty && _hostByRoot.TryGetValue(rootId, out var host)) return _o.Scheme + "://" + host + path;
        return path; // no domain to make it absolute against
    }

    public IdKeyWithCultureId[] GetMatches(string completeUrl) {
        var host = UrlUtil.GetHost(completeUrl);
        var path = UrlUtil.GetPath(completeUrl);
        var rootId = resolveRoot(host);
        if (path.Length <= 1) {
            // the root node itself
            if (rootId == Guid.Empty) return [];
            return [new IdKeyWithCultureId(new NodeKey(rootId), Guid.Empty)];
        }
        var last = UrlUtil.GetLastSegment(path)!;
        var matches = new List<IdKeyWithCultureId>();
        foreach (var candidate in _db.GetNodeIdsFromAddress(last)) {
            if (!tryGetMeta(candidate.IdKey, candidate.CultureId, out var meta)) continue;
            if (!tryBuildPath(meta, out var candidatePath, out var candidateRoot)) continue;
            if (!string.Equals(candidatePath, path, StringComparison.Ordinal)) continue;
            if (rootId != Guid.Empty && candidateRoot != Guid.Empty && candidateRoot != rootId) continue; // other domain
            matches.Add(candidate);
        }
        return [.. matches];
    }

    public bool WillAddressResultInUniqueUrl(NodeKey node, Guid cultureId, string address) {
        var owners = _db.GetNodeIdsFromAddress(address);
        if (owners.Length == 0) return true;
        var self = DefaultUrlManager.ResolveInternalId(_db, node);
        if (self == 0) return true; // the node does not exist yet, so its complete URL cannot be computed; the check runs again on later updates
        if (!tryGetMeta(new NodeKey(self), cultureId, out var meta)) return true;
        if (isRoot(meta.Id)) return true; // a root is served at "/", its address is not part of any URL
        string prospectivePath;
        Guid prospectiveRoot;
        var parent = getParentMeta(meta);
        if (parent == null) {
            prospectivePath = "/" + address;
            prospectiveRoot = Guid.Empty;
        } else {
            if (!tryBuildPath(parent, out var parentPath, out prospectiveRoot)) return true; // parent is not routable, so neither is this node
            prospectivePath = (parentPath == "/" ? "/" : parentPath + "/") + address;
        }
        foreach (var owner in owners) {
            if (owner.IdKey.Int == self) continue; // the node itself, any culture
            if (owner.CultureId != cultureId) continue; // urls are culture scoped
            if (!tryGetMeta(owner.IdKey, owner.CultureId, out var ownerMeta)) continue;
            if (!tryBuildPath(ownerMeta, out var ownerPath, out var ownerRoot)) continue;
            if (ownerRoot != prospectiveRoot) continue; // another domain: the same path is fine
            if (string.Equals(ownerPath, prospectivePath, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    Guid resolveRoot(string? host) {
        if (host != null && _rootByHost.TryGetValue(host, out var rootId)) return rootId;
        return _fallbackRootId;
    }
    bool isRoot(Guid nodeId) {
        if (nodeId == Guid.Empty) return false;
        return _hostByRoot.ContainsKey(nodeId) || nodeId == _fallbackRootId;
    }
    bool tryGetMeta(NodeKey key, Guid cultureId, out NodeMeta meta) {
        var ctx = cultureId == Guid.Empty ? QueryContext.MasterAdmin : QueryContext.MasterAdmin.Culture(cultureId);
        return _db.TryGetNodeMeta(key, out meta!, ctx);
    }
    NodeMeta? getParentMeta(NodeMeta meta) {
        // parent lookup: when the parent is the relation source, the node is the target and the walk goes target -> source
        foreach (var parentId in _db.GetRelatedNodeIdsFromRelationId(_relationId, meta.Id, _o.ParentIsRelationSource)) {
            if (tryGetMeta(new NodeKey(parentId), meta.CultureId, out var parentMeta)) return parentMeta;
            return null;
        }
        return null;
    }
    /// <summary>
    /// The relative URL path of the node: ancestor addresses joined from the domain root (exclusive)
    /// down to the node. False when the node has no URL: an empty address on the chain, a cycle, or
    /// domains are configured and the chain does not end in a configured root.
    /// </summary>
    bool tryBuildPath(NodeMeta meta, out string path, out Guid rootId) {
        path = string.Empty;
        rootId = Guid.Empty;
        var segments = new List<string>(8);
        var current = meta;
        var depth = 0;
        while (true) {
            if (isRoot(current.Id)) {
                rootId = current.Id;
                break;
            }
            if (string.IsNullOrEmpty(current.Address)) return false; // a node without an address has no URL
            segments.Add(current.Address);
            var parent = getParentMeta(current);
            if (parent == null) break; // top of the tree
            current = parent;
            if (++depth > _o.MaxDepth) return false; // cycle guard
        }
        if (rootId == Guid.Empty && (_rootByHost.Count > 0 || _fallbackRootId != Guid.Empty)) {
            return false; // domains are configured and this node is not under any configured root
        }
        if (segments.Count == 0) {
            path = "/";
            return true;
        }
        segments.Reverse();
        path = "/" + string.Join('/', segments);
        return true;
    }
}
