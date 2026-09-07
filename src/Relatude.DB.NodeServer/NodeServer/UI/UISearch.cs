using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The global search of the admin UI: one box in the top bar that looks for a word in everything the
/// UI can open, rather than in one page at a time.
///
/// Three kinds of thing answer, and they are searched in three different ways because they are three
/// different kinds of thing: the data model is a handful of names held in memory (matched here),
/// the settings are a static catalog of paths and explanations (matched here too), and the nodes are
/// the database itself, which has an index for exactly this and is asked through
/// <see cref="UIQuery.QuickNodeSearch"/>. The pages of the UI are matched by the client, which is
/// where their names live.
///
/// Every part is capped and every part fails quietly: this runs on every keystroke, and a closed
/// database or a model that is mid-rebuild must cost the box its node results, not its answer.
/// </summary>
sealed class UISearch {
    readonly RelatudeDBServer _server;
    readonly UIQuery _query;
    internal UISearch(RelatudeDBServer server, UIQuery query) {
        _server = server;
        _query = query;
    }

    // what one group may show before it stops being a glance; the client asks for fewer on a phone
    const int maxPerGroup = 6;
    const int maxNodes = 8;
    // below this a prefix search matches most of the database and none of it usefully
    const int minTextLength = 2;

    internal void Register(UICommands commands) {
        commands.Register("global-search", ctx => search(ctx.Payload<SearchPayload>()));
    }

    object search(SearchPayload p) {
        var text = (p.Text ?? "").Trim();
        if (text.Length < minTextLength) return new { Text = text, Types = Array.Empty<object>(), Properties = Array.Empty<object>(), Settings = Array.Empty<object>(), Nodes = Array.Empty<object>() };
        var max = p.Max is > 0 and <= maxPerGroup ? p.Max.Value : maxPerGroup;
        var dm = datamodel(p.StoreId);
        return new {
            Text = text,
            Types = dm == null ? [] : types(dm, text, max),
            Properties = dm == null ? [] : properties(dm, text, max),
            Settings = settings(p.StoreId, text, max),
            Nodes = p.StoreId is Guid storeId ? _query.QuickNodeSearch(storeId, text, maxNodes) : [],
        };
    }

    /// <summary>The model of the database being looked at, or nothing when it cannot be read now.</summary>
    Datamodel? datamodel(Guid? storeId) {
        if (storeId is not Guid id) return null;
        try {
            return _server.Containers.TryGetValue(id, out var c) ? c.Store?.Datastore.Datamodel : null;
        } catch {
            return null;
        }
    }

    // A name matches from its start before it matches in the middle: typing "prod" is looking for
    // Product, not for ProductGroupIsProductive. Both are shown, in that order, so a memory of half
    // a name still finds it.
    static int rank(string name, string text) {
        var at = name.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? -1 : at == 0 ? 0 : 1;
    }

    object[] types(Datamodel dm, string text, int max) {
        return [.. dm.NodeTypes.Values
            .Where(t => !t.IsInnerNode)
            .Select(t => (Type: t, Rank: rank(t.CodeName, text)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Type.CodeName.Length)
            .ThenBy(x => x.Type.CodeName, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => (object)new {
                x.Type.Id,
                Name = x.Type.CodeName,
                x.Type.FullName,
                Kind = x.Type.ModelType.ToString(),
                x.Type.IsInterface,
                IsBase = x.Type.Id == NodeConstants.BaseNodeTypeId,
            })];
    }

    /// <summary>
    /// The properties and relations whose name matches, each under the type that declares it - so a
    /// property inherited by twenty types is one hit, on the one type the editor would open it on.
    /// </summary>
    object[] properties(Datamodel dm, string text, int max) {
        return [.. dm.Properties.Values
            .Where(p => !p.Internal)
            .Select(p => (Property: p, Rank: rank(p.CodeName, text)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Property.CodeName.Length)
            .ThenBy(x => x.Property.CodeName, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => {
                dm.NodeTypes.TryGetValue(x.Property.NodeType, out var owner);
                return (object)new {
                    x.Property.Id,
                    Name = x.Property.CodeName,
                    TypeId = x.Property.NodeType,
                    TypeName = owner?.CodeName ?? x.Property.NodeType.ToString(),
                    Kind = x.Property is RelationPropertyModel r
                        ? "Relation" + (dm.Relations.TryGetValue(r.RelationId, out var rel) ? " (" + rel.CodeName + ")" : "")
                        : x.Property.PropertyType.ToString(),
                };
            })];
    }

    /// <summary>
    /// The settings whose label, path or explanation mentions the word, from both catalogs: the
    /// database's own settings first when a database is being looked at, then the server's. The value
    /// is not read - this says where a setting is, and the settings page is where it is edited - so
    /// nothing here needs the database to be open.
    /// </summary>
    static object[] settings(Guid? storeId, string text, int max) {
        var found = new List<SettingHit>();
        if (storeId is Guid id) collect(found, SettingsCatalog.Database, "database", id, text);
        collect(found, SettingsCatalog.Server, "server", null, text);
        // the label is what someone is most likely typing, so those come first whichever catalog
        // they are in, and a hit only in the help text comes last
        return [.. found
            .OrderBy(hit => hit.Rank)
            .Take(max)
            .Select(hit => (object)new { hit.Scope, hit.StoreId, hit.SectionId, hit.SectionTitle, hit.GroupId, hit.GroupTitle, hit.Path, hit.Label, hit.Help })];
    }

    sealed record SettingHit(string Scope, Guid? StoreId, string SectionId, string SectionTitle, string GroupId, string GroupTitle, string Path, string Label, string Help, int Rank);

    static void collect(List<SettingHit> found, SettingSectionDefinition[] catalog, string scope, Guid? storeId, string text) {
        foreach (var section in catalog) {
            foreach (var group in section.Groups) {
                foreach (var definition in group.Settings) add(found, scope, storeId, section, group, definition, text);
                // a list's fields are settings on longer paths; they live in the same group
                foreach (var definition in group.List?.Fields ?? []) add(found, scope, storeId, section, group, definition, text);
            }
        }
    }

    static void add(List<SettingHit> found, string scope, Guid? storeId, SettingSectionDefinition section, SettingGroupDefinition group, SettingDefinition definition, string text) {
        var r = settingRank(definition, group, section, text);
        if (r < 0) return;
        found.Add(new SettingHit(scope, storeId, section.Id, section.Title, group.Id, group.Title, definition.Path, definition.Label, definition.Help, r));
    }

    // 0-1: the label, from its start or inside it. 2: the path, which is what the settings file and
    // the configuration overlay call it. 3: the group or section it is in - the whole group is worth
    // offering for a word like "backup". 4: the explanation, which mentions far more than it is about.
    static int settingRank(SettingDefinition definition, SettingGroupDefinition group, SettingSectionDefinition section, string text) {
        var label = rank(definition.Label, text);
        if (label >= 0) return label;
        if (rank(definition.Path, text) >= 0) return 2;
        if (rank(group.Title, text) >= 0 || rank(section.Title, text) >= 0) return 3;
        if (definition.Help.Contains(text, StringComparison.OrdinalIgnoreCase)) return 4;
        return -1;
    }

    internal sealed record SearchPayload(Guid? StoreId, string? Text, int? Max);
}
