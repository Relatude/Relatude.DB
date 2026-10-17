using Relatude.DB.Nodes;

namespace Relatude.DB.NodeServer.Settings;

/// <summary>
/// The custom settings the application has registered (<see cref="ServerOptions.AddCustomSettings"/>),
/// reached as <see cref="RelatudeDBServer.CustomSettings"/>, and the sets of them in force: one for a
/// definition of the server's, one per database for a definition of each database's. Code with a store
/// at hand reads them as <c>db.Settings.Get&lt;T&gt;()</c>; this is for code without one.
/// <para>The sets are read when the server starts and again at every soft restart; a database added
/// later has its sets read the first time they are asked for.</para>
/// </summary>
public sealed class CustomSettingsRegistry {
    readonly RelatudeDBServer _server;
    readonly object _lock = new();
    ICustomSettingsDefinition[] _definitions = [];
    Dictionary<string, ICustomSettingsDefinition> _byKey = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<Type, ICustomSettingsDefinition> _byType = [];
    Dictionary<(string Key, Guid StoreId), CustomSettingsSet> _sets = [];

    internal CustomSettingsRegistry(RelatudeDBServer server) => _server = server;

    /// <summary>Every definition registered, in the order they were.</summary>
    public IReadOnlyList<ICustomSettingsDefinition> Definitions => _definitions;

    /// <summary>The definition with this file key, or null.</summary>
    public ICustomSettingsDefinition? Find(string fileKey) => _byKey.GetValueOrDefault(fileKey);
    /// <summary>The definition whose settings are of this type, or null.</summary>
    public ICustomSettingsDefinition? Find(Type settingsType) => _byType.GetValueOrDefault(settingsType);

    /// <summary>
    /// The settings of type <typeparamref name="T"/> in force: the server's for a set of the server's, and
    /// for a set per database those of <paramref name="storeId"/>, or of the default database when it is
    /// null. Throws when no set of that type is registered, or its files cannot be read.
    /// </summary>
    public T Get<T>(Guid? storeId = null) where T : class {
        var definition = Find(typeof(T)) ?? throw new InvalidOperationException("No settings of type " + typeof(T).FullName + " are registered. ");
        return (T)SetFor(definition, storeId).Value;
    }

    /// <summary>The set of <paramref name="fileKey"/> for <paramref name="storeId"/>; see <see cref="SetFor(ICustomSettingsDefinition, Guid?)"/>.</summary>
    public CustomSettingsSet SetFor(string fileKey, Guid? storeId = null)
        => SetFor(Find(fileKey) ?? throw new InvalidOperationException("No custom settings have the file key \"" + fileKey + "\". "), storeId);

    /// <summary>
    /// The set of a definition in force: the server's - <paramref name="storeId"/> is not looked at - or
    /// that of the database <paramref name="storeId"/>, the default one when it is null.
    /// </summary>
    public CustomSettingsSet SetFor(ICustomSettingsDefinition definition, Guid? storeId = null) {
        var id = Guid.Empty; // the server's sets
        if (definition.Scope == CustomSettingsScope.Database) {
            id = storeId ?? _server.Settings.DefaultStoreId;
            if (!_server.Containers.ContainsKey(id)) throw new InvalidOperationException("Database not found. ");
        }
        var key = (definition.FileKey, id);
        lock (_lock) {
            if (_sets.TryGetValue(key, out var set)) return set;
        }
        // read outside the lock: it reads files, and may reach a blob container
        var loaded = _server.LoadCustomSettings(definition, definition.Scope == CustomSettingsScope.Database ? id : null);
        lock (_lock) {
            if (_sets.TryGetValue(key, out var set)) return set; // another thread read it first
            _sets[key] = loaded;
            return loaded;
        }
    }

    /// <summary>What a store of the database <paramref name="storeId"/> reads its <see cref="NodeStore.Settings"/> from.</summary>
    public ICustomSettingsSource SourceFor(Guid storeId) => new Source(this, storeId);

    sealed class Source(CustomSettingsRegistry registry, Guid storeId) : ICustomSettingsSource {
        public object? Get(Type settingsType) {
            var definition = registry.Find(settingsType);
            return definition == null ? null : registry.SetFor(definition, storeId).Value;
        }
    }

    /// <summary>Takes the definitions the server starts with. Throws on any that is wrong - a file key
    /// that cannot name a file, two sets with one key or type, a path that names no property - so a
    /// mistake is seen the first time the application runs.</summary>
    internal void Register(IEnumerable<ICustomSettingsDefinition> definitions) {
        var list = definitions.Distinct().ToArray(); // one instance registered both ways counts once
        var problems = list.SelectMany(CustomSettingsCatalog.Problems).ToList();
        foreach (var group in list.GroupBy(d => d.FileKey, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) {
            problems.Add("Two sets of custom settings have the file key \"" + group.Key + "\": " + string.Join(" and ", group.Select(d => d.GetType().Name)) + ".");
        }
        foreach (var group in list.GroupBy(d => d.SettingsType).Where(g => g.Count() > 1)) {
            problems.Add("Two sets of custom settings keep their values in " + group.Key.FullName + ": " + string.Join(" and ", group.Select(d => d.GetType().Name))
                + ". db.Settings.Get<T>() finds a set by its type, so each needs a class of its own.");
        }
        if (problems.Count > 0) throw new InvalidOperationException("The custom settings registered are not right:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        lock (_lock) {
            _definitions = list;
            _byKey = list.ToDictionary(d => d.FileKey, StringComparer.OrdinalIgnoreCase);
            _byType = list.ToDictionary(d => d.SettingsType);
            _sets = [];
        }
    }

    /// <summary>Forgets every set read, and reads them again: the server's, and those of every database.
    /// At start and at every soft restart, after the server's own settings are read.</summary>
    internal void Reload() {
        lock (_lock) _sets = [];
        foreach (var definition in _definitions) {
            if (definition.Scope == CustomSettingsScope.Server) {
                SetFor(definition);
                continue;
            }
            foreach (var container in _server.GetContainers()) SetFor(definition, container.Settings.Id);
        }
    }

    /// <summary>The sets read so far.</summary>
    internal CustomSettingsSet[] LoadedSets() {
        lock (_lock) return [.. _sets.Values];
    }
}
