using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.Nodes;

/// <summary>
/// Where <see cref="NodeStoreSettings"/> finds the settings in force. The server implements it over the
/// settings the application registers (<c>ServerOptions.AddCustomSettings</c>), so a store only ever asks
/// for a type and never learns where its values came from.
/// </summary>
public interface ICustomSettingsSource {
    /// <summary>
    /// The settings of this type in force for the database the store belongs to - the server's own for a
    /// set registered for the whole server - or null when no set of this type is registered. Throws when
    /// one is registered but its files cannot be read.
    /// </summary>
    object? Get(Type settingsType);
}

/// <summary>
/// The settings the application has registered for itself, reached as <see cref="NodeStore.Settings"/>:
/// <code>
/// var shop = db.Settings.Get&lt;WebshopSettings&gt;();
/// if (shop.FreeShippingAbove &lt;= cart.Total) ...
/// </code>
/// <para>What comes back is the settings object in force, with every layer over it - the settings file
/// deployed with the application, the changes saved in the admin UI, the configuration section and the
/// application's own code. It is replaced, not changed, when the admin UI saves, so asking again is how a
/// change is seen; it is cheap enough to ask for on every request. Treat it as read only: a change made to
/// it is not saved, and is lost at the next save from the admin UI.</para>
/// </summary>
public sealed class NodeStoreSettings {
    readonly ICustomSettingsSource? _source;

    internal NodeStoreSettings(ICustomSettingsSource? source) => _source = source;

    /// <summary>The settings of type <typeparamref name="T"/> in force. Throws when no set of that type is
    /// registered, or its files cannot be read.</summary>
    public T Get<T>() where T : class {
        if (TryGet<T>(out var settings)) return settings;
        throw new InvalidOperationException("No settings of type " + typeof(T).FullName + " are registered. Register a definition for them with "
            + "ServerOptions.AddCustomSettings in AddRelatudeDB, or as an ICustomSettingsDefinition service. ");
    }

    /// <summary>The settings of type <typeparamref name="T"/> in force, when a set of that type is registered.
    /// Still throws when one is registered but its files cannot be read.</summary>
    public bool TryGet<T>([NotNullWhen(true)] out T? settings) where T : class {
        settings = _source?.Get(typeof(T)) as T;
        return settings != null;
    }

    /// <summary>Whether a set of settings of type <typeparamref name="T"/> is registered.</summary>
    public bool IsRegistered<T>() where T : class => _source?.Get(typeof(T)) != null;
}
