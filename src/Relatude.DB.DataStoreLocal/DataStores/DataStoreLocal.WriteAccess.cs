using System.Runtime.CompilerServices;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Indexes.Meta;
using Relatude.DB.DataStores.Transactions;
using Relatude.DB.Native;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores;

// Write access: checked once a transaction's primitives have run and before it commits, comparing each node as it
// was with what the transaction leaves, so every kind of write is judged alike. The rules are in the manual's
// "Who may write".
public sealed partial class DataStoreLocal {
    readonly record struct Writer(Guid UserId, SystemUserType Type, Guid[]? Groups) {
        public bool IsAdmin => Type == SystemUserType.Admin;
        public bool IsIn(Guid group) => NodeTypesByIds.IsUserAMember(group, Type, Groups);
    }
    // resolved before the transaction runs, so one that puts its own user in a group gains nothing by it in that transaction
    Writer writerOf(QueryContext ctx) {
        var (type, groups) = _nativeModelStore.WriterOf(ctx.UserId);
        return new(ctx.UserId, type, groups);
    }

    public NodeWriteRights? GetWriteRights(Guid nodeId, QueryContext? ctx = null) {
        _lock.EnterReadLock();
        try {
            if (!_guids.TryGetId(nodeId, out int id) || storedOf(id) is not { } node) return null;
            var writer = writerOf(ctx ?? _defaultQueryCtx);
            if (writer.IsAdmin) return new(true, true);
            var edit = groupsOf(node, m => m.EditAccess, t => t.DefaultEditAccess).All(writer.IsIn);
            return new(edit, edit && groupsOf(node, m => m.PublishAccess, t => t.DefaultPublishAccess).All(writer.IsIn));
        } finally {
            _lock.ExitReadLock();
        }
    }
    public TypeWriteRights? GetTypeWriteRights(Guid nodeTypeId, QueryContext? ctx = null) {
        if (!_definition.NodeTypes.TryGetValue(nodeTypeId, out var definition)) return null;
        var type = definition.Model;
        _lock.EnterReadLock();
        try {
            var writer = writerOf(ctx ?? _defaultQueryCtx);
            if (writer.IsAdmin) return new(true, true, []);
            var create = writer.IsIn(typeGroup(type.DefaultEditAccess));
            Guid[] readOnly = [.. guardedOf(type).Where(p => p.Id == NodeConstants.NativeUserPropertyUserType || !writer.IsIn(p.WriteAccess)).Select(p => p.Id)];
            return new(create, create && writer.IsIn(typeGroup(type.DefaultPublishAccess)), readOnly);
        } finally {
            _lock.ExitReadLock();
        }
    }

    void validateWriteAccess(List<PrimitiveActionBase> executed, Writer writer) {
        if (writer.IsAdmin) return;
        Dictionary<int, (INodeData? Before, INodeData? After)>? nodes = null;
        Dictionary<(Guid, int, int), int>? links = null;
        List<PrimitiveRelationReorderAction>? reorders = null;
        foreach (var action in executed) {
            switch (action) {
                case PrimitiveNodeAction na: {
                        nodes ??= [];
                        var id = na.Node.__Id;
                        var after = na.Operation == PrimitiveOperation.Add ? na.Node : null;
                        // the first primitive of a node says how it was before: removed, it was there; added, it was made here
                        nodes[id] = nodes.TryGetValue(id, out var seen) ? (seen.Before, after) : (na.Operation == PrimitiveOperation.Remove ? na.Node : null, after);
                        break;
                    }
                case PrimitiveRelationAction ra: {
                        links ??= [];
                        var key = (ra.RelationId, ra.Source, ra.Target);
                        links[key] = links.GetValueOrDefault(key) + (ra.Operation == PrimitiveOperation.Add ? 1 : -1);
                        break;
                    }
                case PrimitiveRelationReorderAction rr:
                    (reorders ??= []).Add(rr);
                    break;
            }
        }
        if (nodes != null) foreach (var (_, (before, after)) in nodes) validateNodeWrite(before, after, writer);
        if (links != null) {
            foreach (var ((_, source, target), net) in links) {
                if (net == 0) continue; // linked and unlinked again
                if (mayLink(source, nodes, writer) || mayLink(target, nodes, writer)) continue;
                throw new WriteAccessException(whoIs(writer) + " may not link or unlink the nodes " + nodeName(source) + " and " + nodeName(target)
                    + ": that takes edit access to one of them. ", guidOf(source), WriteAccessRight.Relation, editGroupOf(source, nodes));
            }
        }
        if (reorders != null) {
            foreach (var rr in reorders) {
                if (mayLink(rr.Owner, nodes, writer)) continue;
                throw new WriteAccessException(whoIs(writer) + " may not reorder the links of " + nodeName(rr.Owner) + ": that takes edit access to it. ",
                    guidOf(rr.Owner), WriteAccessRight.Relation, editGroupOf(rr.Owner, nodes));
            }
        }
    }

    void validateNodeWrite(INodeData? before, INodeData? after, Writer writer) {
        if (before == null && after == null) return; // made and deleted again
        if (before == null) {
            var type = typeOf(after!);
            mayCreate(type, after!, writer);
            if (publishedOf(after).Any()) {
                var publish = typeGroup(type.DefaultPublishAccess);
                if (!writer.IsIn(publish)) throw refused(writer, after!, WriteAccessRight.Publish, publish, " may not make a published node of the type " + type.CodeName);
            }
            validateValuesBrought(null, after!, writer);
            return;
        }
        foreach (var edit in groupsOf(before, m => m.EditAccess, t => t.DefaultEditAccess)) {
            if (!writer.IsIn(edit)) throw refused(writer, before, WriteAccessRight.Edit, edit, " may not change " + describe(before));
        }
        if (after != null && after.NodeType != before.NodeType) mayCreate(typeOf(after), after, writer);
        var publishGroups = groupsOf(before, m => m.PublishAccess, t => t.DefaultPublishAccess);
        if (!publishGroups.All(writer.IsIn)) {
            var publish = publishGroups.First(g => !writer.IsIn(g));
            if (after == null) {
                if (publishedOf(before).Any()) throw refused(writer, before, WriteAccessRight.Publish, publish, " may not delete " + describe(before) + ", which readers see");
            } else if (publishedChanged(before, after)) {
                throw refused(writer, before, WriteAccessRight.Publish, publish, " may not change what readers see of " + describe(before));
            } else if (accessChanged(before, after)) {
                throw refused(writer, before, WriteAccessRight.Publish, publish, " may not change who may read, edit or publish " + describe(before));
            }
        }
        if (after != null) validateValuesBrought(before, after, writer);
    }
    void mayCreate(NodeTypeModel type, INodeData node, Writer writer) {
        var create = typeGroup(type.DefaultEditAccess);
        if (!writer.IsIn(create)) throw refused(writer, node, WriteAccessRight.Create, create, " may not make nodes of the type " + type.CodeName);
    }
    // a link may be changed by whoever may edit either node; one the transaction makes or deletes passed its own check
    bool mayLink(int id, Dictionary<int, (INodeData? Before, INodeData? After)>? nodes, Writer writer) {
        INodeData? state;
        if (nodes != null && nodes.TryGetValue(id, out var change)) {
            if (change.Before == null || change.After == null) return true;
            state = change.Before;
        } else {
            state = storedOf(id);
            if (state == null) return true; // no node to protect: the relation itself says what is wrong
        }
        return groupsOf(state, m => m.EditAccess, t => t.DefaultEditAccess).All(writer.IsIn);
    }
    Guid editGroupOf(int id, Dictionary<int, (INodeData? Before, INodeData? After)>? nodes) {
        var state = (nodes != null && nodes.TryGetValue(id, out var change) ? change.Before : null) ?? storedOf(id);
        return state == null ? Guid.Empty : groupsOf(state, m => m.EditAccess, t => t.DefaultEditAccess).First();
    }
    INodeData? storedOf(int id) => _nodes.TryGet(id, out var node, out _) ? node : null;

    NodeTypeModel typeOf(INodeData node) => _definition.NodeTypes[node.NodeType].Model;
    Guid typeGroup(Guid fromType) => NodeTypesByIds.FindUserGroup(NodeConstants.UserGroupUnspecified, fromType, _settings.DefaultWriteAccess);
    // the groups a node's versions name (its access is the same for all of them, but a store may hold otherwise)
    Guid[] groupsOf(INodeData node, Func<IInnerNodeMeta, Guid> fromMeta, Func<NodeTypeModel, Guid> fromType) {
        var type = typeOf(node);
        var fallback = fromType(type);
        if (node is not NodeDataRevisions revisions) return [NodeTypesByIds.FindUserGroup(fromMeta(node.Meta ?? IInnerNodeMeta.Empty), fallback, _settings.DefaultWriteAccess)];
        if (revisions.Revisions.Length == 0) return [NodeTypesByIds.FindUserGroup(NodeConstants.UserGroupUnspecified, fallback, _settings.DefaultWriteAccess)];
        return [.. revisions.Revisions.Select(r => NodeTypesByIds.FindUserGroup(fromMeta(r.Meta ?? IInnerNodeMeta.Empty), fallback, _settings.DefaultWriteAccess)).Distinct()];
    }
    static IEnumerable<INodeData> versionsOf(INodeData? node) {
        if (node == null) return [];
        if (node is NodeDataRevisions revisions) return revisions.Revisions;
        return [node];
    }
    // what readers can read of a node: its published versions, a node without revisions being one, unless deleted
    static IEnumerable<INodeData> publishedOf(INodeData? node) => versionsOf(node).Where(v =>
        (v.Meta ?? IInnerNodeMeta.Empty) is var m && m.RevisionType == RevisionType.Published && !m.Deleted);
    bool publishedChanged(INodeData before, INodeData after) {
        var was = publishedOf(before).ToList();
        var now = publishedOf(after).ToList();
        if (was.Count != now.Count) return true;
        foreach (var b in was) {
            var culture = b.Meta?.CultureId ?? Guid.Empty;
            var a = now.FirstOrDefault(n => (n.Meta?.CultureId ?? Guid.Empty) == culture);
            if (a == null) return true;
            if (Utils.AreDifferentIgnoringGeneratedPropsAndMeta(a, b, _definition)) return true;
            if (a.Meta?.ReleaseUtc != b.Meta?.ReleaseUtc || a.Meta?.ExpireUtc != b.Meta?.ExpireUtc) return true;
        }
        return false;
    }
    static bool accessChanged(INodeData before, INodeData after) {
        static (Guid, Guid, Guid, Guid) accessOf(INodeData v) => v.Meta is { } m ? (m.ReadAccess, m.EditAccess, m.EditViewAccess, m.PublishAccess) : default;
        return !versionsOf(before).Select(accessOf).ToHashSet().SetEquals(versionsOf(after).Select(accessOf));
    }

    // a property with a write group of its own, and a user's type, guard the values written into them: a value the node
    // did not hold before (on a node made here, one other than the property's default)
    void validateValuesBrought(INodeData? before, INodeData after, Writer writer) {
        var type = typeOf(after);
        foreach (var property in guardedOf(type)) {
            var isUserType = property.Id == NodeConstants.NativeUserPropertyUserType;
            if (!isUserType && writer.IsIn(property.WriteAccess)) continue;
            var definition = _definition.Properties[property.Id];
            var held = valuesOf(before, property.Id).ToList();
            if (before == null && type.GetDefaultValue(property) is { } byDefault) held.Add(byDefault);
            foreach (var value in valuesOf(after, property.Id)) {
                if (held.Any(h => definition.AreValuesEqual(value, h))) continue;
                if (isUserType) {
                    // a user may be made by anyone allowed to, but only as a user, and only administrators change a user's type
                    if (before == null && value is int t && t != (int)SystemUserType.Admin) continue;
                    throw refused(writer, after, WriteAccessRight.UserType, NodeConstants.UserGroupAdmins,
                        before == null ? " may not make an administrator" : " may not change the user type of " + describe(after));
                }
                throw new WriteAccessException(whoIs(writer) + " may not write the property " + property.CodeName + " of " + describe(after)
                    + ": that takes the group " + groupName(property.WriteAccess) + ". ", after.Id, WriteAccessRight.Property, property.WriteAccess, property.Id);
            }
        }
    }
    static IEnumerable<object> valuesOf(INodeData? node, Guid propertyId) {
        foreach (var version in versionsOf(node)) if (version.TryGetValue(propertyId, out var value)) yield return value;
    }
    // the properties of a type whose values are guarded; empty for most types
    readonly ConditionalWeakTable<NodeTypeModel, PropertyModel[]> _guardedProperties = new();
    PropertyModel[] guardedOf(NodeTypeModel type) => _guardedProperties.GetValue(type, t => [.. t.AllProperties.Values
        .Where(p => p.WriteAccess != NodeConstants.UserGroupUnspecified || p.Id == NodeConstants.NativeUserPropertyUserType)]);

    WriteAccessException refused(Writer writer, INodeData node, WriteAccessRight right, Guid group, string what) {
        var takes = right == WriteAccessRight.UserType ? "only administrators do" : "that takes the group " + groupName(group);
        return new(whoIs(writer) + what + ": " + takes + ". ", node.Id, right, group);
    }
    string describe(INodeData node) {
        var name = versionsOf(node).Select(NodeName.Of).FirstOrDefault(n => n != null);
        return "the node " + (name != null ? "\"" + name + "\" (" + node.Id + ")" : node.Id.ToString()) + " of the type " + typeOf(node).CodeName;
    }
    string nodeName(int id) => storedOf(id) is { } node ? describe(node) : "the node " + id;
    Guid guidOf(int id) => _guids.TryGetId(id, out Guid guid) ? guid : Guid.Empty;
    string whoIs(Writer writer) {
        if (writer.UserId == Guid.Empty) return "An anonymous user";
        var name = _guids.TryGetId(writer.UserId, out int id) && storedOf(id) is { } user ? versionsOf(user).Select(NodeName.Of).FirstOrDefault(n => n != null) : null;
        return "The user " + (name != null ? "\"" + name + "\" (" + writer.UserId + ")" : writer.UserId.ToString());
    }
    string groupName(Guid group) {
        if (group == NodeConstants.UserGroupEveryone) return "Everyone";
        if (group == NodeConstants.UserGroupMember) return "Members";
        if (group == NodeConstants.UserGroupAdmins) return "Administrators";
        var name = _guids.TryGetId(group, out int id) && storedOf(id) is { } node ? versionsOf(node).Select(NodeName.Of).FirstOrDefault(n => n != null) : null;
        return name != null ? "\"" + name + "\" (" + group + ")" : group.ToString();
    }

    // before a file is stored or analysed, so a refused upload stores no bytes and an analysis spends no credits; the
    // transaction that puts the file in is checked in full
    void ensureMayWriteFile(PropertyPath path, QueryContext ctx) {
        _lock.EnterReadLock();
        try {
            var writer = writerOf(ctx);
            if (writer.IsAdmin) return;
            var key = path.NodePath.NodeKey;
            var id = key.HasInt ? key.Int : _guids.TryGetId(key.Guid, out int byGuid) ? byGuid : 0;
            if (storedOf(id) is not { } node) return; // the write says it is missing
            foreach (var edit in groupsOf(node, m => m.EditAccess, t => t.DefaultEditAccess)) {
                if (!writer.IsIn(edit)) throw refused(writer, node, WriteAccessRight.Edit, edit, " may not change " + describe(node));
            }
            if (path.NodePath.Path.Length == 0 && typeOf(node).AllProperties.TryGetValue(path.PropertyId, out var property)
                && property.WriteAccess != NodeConstants.UserGroupUnspecified && !writer.IsIn(property.WriteAccess)) {
                throw new WriteAccessException(whoIs(writer) + " may not write the property " + property.CodeName + " of " + describe(node)
                    + ": that takes the group " + groupName(property.WriteAccess) + ". ", node.Id, WriteAccessRight.Property, property.WriteAccess, property.Id);
            }
        } finally {
            _lock.ExitReadLock();
        }
    }
}
