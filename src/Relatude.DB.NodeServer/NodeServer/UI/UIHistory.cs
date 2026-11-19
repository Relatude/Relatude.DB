using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The change history of one database in the admin UI (Activity › History, Storage › Rebuild history index): its
/// status, the changes newest first, the activity in bars, the types it records and a rebuild. All through the
/// history API of the database's store, which the admin UI asks as the database's administrator.
/// </summary>
sealed class UIHistory {
    readonly RelatudeDBServer _server;
    internal UIHistory(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("history-status", ctx => status(ctx.Payload<StorePayload>().StoreId));
        commands.Register("history-changes", ctx => changes(ctx.Payload<ChangesPayload>()));
        commands.Register("history-activity", ctx => activity(ctx.Payload<ActivityPayload>()));
        commands.Register("history-types", ctx => types(ctx.Payload<StorePayload>().StoreId));
        commands.Register("history-rebuild", ctx => rebuild(ctx.Payload<StorePayload>().StoreId));
    }

    NodeStoreContainer container(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        return c;
    }
    IDataStore datastore(Guid storeId) {
        var store = container(storeId).Store;
        if (store == null || store.Datastore.State != Common.DataStoreState.Open) throw new Exception("The database is not open. ");
        return store.Datastore;
    }

    object status(Guid storeId) {
        var c = container(storeId);
        var local = c.Settings.LocalSettings;
        var history = local?.History ?? new();
        var store = c.Store;
        var open = store != null && store.Datastore.State == Common.DataStoreState.Open;
        return new {
            Open = open,
            Settings = new { history.Enabled, history.TypesByDefault, BuildSpeed = history.BuildSpeed.ToString(), SecondaryLog = local?.SecondaryBackupLog ?? false },
            Status = open ? store!.Datastore.GetLogHistoryStatus() : null,
        };
    }

    object rebuild(Guid storeId) {
        datastore(storeId).RebuildLogHistory();
        return new { Started = true };
    }

    // ---- the changes ----

    internal sealed record ChangesPayload(Guid StoreId, DateTime? FromUtc, DateTime? ToUtc, Guid[]? Types, Guid[]? Writers, string[]? Kinds, Guid? Node,
        string? Cursor, int? Take, bool? Collapse, bool? IncludeSystem, long? Transaction, bool? WithLinks);
    internal sealed record ActivityPayload(Guid StoreId, DateTime FromUtc, DateTime ToUtc, string? Interval, string? By, Guid[]? Types, Guid[]? Writers, string[]? Kinds, bool? IncludeSystem);

    static LogChangeKinds kindsOf(string[]? names) {
        if (names is not { Length: > 0 }) return LogChangeKinds.All;
        var kinds = LogChangeKinds.None;
        foreach (var n in names) if (Enum.TryParse<LogChangeKinds>(n, true, out var k)) kinds |= k;
        // a node deleted for good is one removed
        if ((kinds & LogChangeKinds.Removed) != 0) kinds |= LogChangeKinds.NodeRemoved;
        return kinds == LogChangeKinds.None ? LogChangeKinds.All : kinds;
    }
    static string[] kindNames(LogChangeKinds kinds) => [.. Enum.GetValues<LogChangeKinds>().Where(k => k != LogChangeKinds.None && k != LogChangeKinds.All && (kinds & k) == k).Select(k => k.ToString())];

    object changes(ChangesPayload p) {
        var ds = datastore(p.StoreId);
        var page = ds.ReadLogChanges(new LogChangeQuery {
            FromUtc = p.FromUtc, ToUtc = p.ToUtc, NodeTypes = p.Types, Writers = p.Writers, Nodes = p.Node is { } n ? [n] : null,
            Kinds = kindsOf(p.Kinds), Cursor = p.Cursor, Take = p.Take ?? 50, Collapse = p.Collapse ?? true, IncludeSystemWrites = p.IncludeSystem ?? false,
            Transaction = p.Transaction, WithLinks = p.WithLinks ?? true, WithDetails = true,
        });
        var dm = ds.Datamodel;
        var names = new Names(ds);
        return new {
            Changes = page.Changes.Select(c => new {
                c.Timestamp, c.Utc, c.NodeId, TypeId = c.NodeType, TypeName = typeName(dm, c.NodeType), c.RevisionId,
                Kinds = kindNames(c.Kind), c.Writer, WriterName = names.Of(c.Writer),
                c.Culture, CultureName = names.Of(c.Culture), c.Collection, CollectionName = names.Of(c.Collection),
                TypeAfter = c.TypeAfter?.ToString(), TypeBefore = c.TypeBefore?.ToString(), c.InTransaction, c.Name, c.VersionKept, c.Versions, c.Others,
                ChangedProperties = c.ChangedProperties?.Select(id => new { Id = id, Name = propertyName(dm, c.NodeType, id) }),
                TypeCounts = c.TypeCounts?.Select(t => new { TypeId = t.NodeType, TypeName = typeName(dm, t.NodeType), t.Count }),
            }),
            Links = page.Links.Select(l => new {
                l.Timestamp, l.Utc, l.RelationId, RelationName = dm.Relations.TryGetValue(l.RelationId, out var r) ? r.CodeName : l.RelationId.ToString(),
                l.Source, SourceName = names.Of(l.Source), SourceTypeName = typeName(dm, l.SourceType),
                l.Target, TargetName = names.Of(l.Target), TargetTypeName = typeName(dm, l.TargetType),
                l.Added, l.Writer, WriterName = names.Of(l.Writer), l.InTransaction,
            }),
            page.Next,
        };
    }

    object activity(ActivityPayload p) {
        var ds = datastore(p.StoreId);
        var result = ds.AnalyseLogChanges(new LogActivityQuery {
            FromUtc = p.FromUtc, ToUtc = p.ToUtc,
            Interval = Enum.TryParse<LogActivityInterval>(p.Interval, true, out var i) ? i : LogActivityInterval.Day,
            By = Enum.TryParse<LogActivityBy>(p.By, true, out var b) ? b : LogActivityBy.None,
            NodeTypes = p.Types, Writers = p.Writers, Kinds = kindsOf(p.Kinds), IncludeSystemWrites = p.IncludeSystem ?? false,
        });
        var dm = ds.Datamodel;
        var names = new Names(ds);
        string label(string key) {
            if (key == "other") return "Other";
            if (!Guid.TryParse(key, out var id)) return key;
            if (id == Guid.Empty) return result.By == LogActivityBy.Writer ? "Not recorded" : "None";
            return result.By == LogActivityBy.Type ? typeName(dm, id) : names.Of(id) ?? key;
        }
        return new {
            Interval = result.Interval.ToString(), result.FromUtc, result.ToUtc, By = result.By.ToString(), result.Totals,
            Groups = result.Groups.Select(g => new { g.Key, Label = label(g.Key), g.Changes }),
            Buckets = result.Buckets.Select(x => new { x.StartUtc, x.Counts, x.Groups }),
        };
    }

    // ---- the types it records ----

    object types(Guid storeId) {
        var c = container(storeId);
        var ds = datastore(storeId);
        var dm = ds.Datamodel;
        var byDefault = c.Settings.LocalSettings?.History?.TypesByDefault ?? true;
        var resolved = dm.ResolveSwitch(t => t.History, nameof(NodeTypeModel.History), byDefault);
        var overridden = dm.Overrides?.NodeTypes.Where(kv => kv.Value.History != null).Select(kv => kv.Key).ToHashSet() ?? [];
        return new {
            TypesByDefault = byDefault,
            Types = dm.NodeTypes.Values.Where(t => !t.IsInnerNode).OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase).Select(t => {
                string from;
                string? fromType = null;
                if (overridden.Contains(t.Id)) from = "override";
                else if (t.History.HasValue) from = "code";
                else {
                    var setter = t.ThisAndAllInheritedTypes.Values.Where(x => x != t && x.History.HasValue)
                        .OrderByDescending(x => x.ThisAndAllInheritedTypes.Count).FirstOrDefault();
                    from = setter == null ? "default" : "inherited";
                    fromType = setter?.CodeName;
                }
                return new {
                    t.Id, Name = t.CodeName, t.FullName, Own = t.History, InHistory = resolved.TryGetValue(t.Id, out var v) ? v : byDefault,
                    From = from, FromType = fromType, t.IsAbstract,
                    Parents = t.Parents,
                };
            }),
        };
    }

    static string typeName(Datamodel dm, Guid id) => id == Guid.Empty ? "" : dm.NodeTypes.TryGetValue(id, out var t) ? t.CodeName : id.ToString();
    static string? propertyName(Datamodel dm, Guid typeId, Guid propertyId) =>
        dm.NodeTypes.TryGetValue(typeId, out var t) && t.AllProperties.TryGetValue(propertyId, out var p) ? p.CodeName : null;

    /// <summary>The names of the people, languages, sites and nodes a page names, each read once.</summary>
    sealed class Names(IDataStore ds) {
        readonly Dictionary<Guid, string?> _names = [];
        public string? Of(Guid? id) {
            if (id is not { } g || g == Guid.Empty) return null;
            if (g == NodeConstants.MasterAdminUserId) return "Database administrator";
            if (_names.TryGetValue(g, out var known)) return known;
            string? name = null;
            try {
                if (ds.TryGet(g, out var node, QueryContext.AllContent) && node.TryGetValue(NodeConstants.NamePropertyId, out var value)) name = value as string;
            } catch { }
            _names[g] = name;
            return name;
        }
    }

    sealed record StorePayload(Guid StoreId);
}
