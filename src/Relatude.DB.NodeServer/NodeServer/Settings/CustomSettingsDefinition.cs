using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Relatude.DB.NodeServer.Settings;

/// <summary>Whether a set of custom settings is one for the whole server, or one per database.</summary>
public enum CustomSettingsScope {
    /// <summary>One set for the whole server, beside the server settings.</summary>
    Server,
    /// <summary>One set per database, beside each database's settings, with values of its own.</summary>
    Database,
}

/// <summary>
/// A set of settings an application registers for itself - a web shop's currency, its payment
/// provider's keys, the address orders are mailed to - so they are edited on a tab of their own in the
/// admin UI, beside the server and database settings, and read from code as
/// <c>db.Settings.Get&lt;TSettings&gt;()</c>. Implement <see cref="ICustomSettingsDefinition{TSettings}"/>,
/// and register it with <see cref="ServerOptions.AddCustomSettings"/> or as an
/// <see cref="ICustomSettingsDefinition"/> service.
///
/// <para>The values are layered exactly as the server's own settings are, every layer over the one
/// before:</para>
/// <list type="number">
/// <item>the defaults of the settings class: what a new instance holds;</item>
/// <item>SHARED - <c>relatude.settings/{FileKey}.json</c>, or <c>relatude.settings/{short name}/{FileKey}.json</c>
/// for a set per database: part of the application, kept in source control and deployed with it;</item>
/// <item>THIS SERVER - <c>overrides/{FileKey}.overrides.json</c>, beside relatude.db.overrides.json for a server
/// set and on the database's own storage for a set per database: what the admin UI saved, as the
/// difference from SHARED. It can be moved into SHARED from the tab;</item>
/// <item>APPSETTINGS - the <c>RelatudeDB:CustomSettings:{FileKey}</c> configuration section (every database,
/// for a set per database), and <c>RelatudeDB:CustomSettings:Databases:{short name or id}:{FileKey}</c> for
/// one database only. Never written to either file;</item>
/// <item>CODE - <see cref="ICustomSettingsDefinition{TSettings}.OnInit"/>, run at every start. Never written
/// either.</item>
/// </list>
/// </summary>
/// <remarks>The non-generic face of a definition, which is what the server keeps; implement
/// <see cref="ICustomSettingsDefinition{TSettings}"/>, which fills in <see cref="SettingsType"/> and
/// <see cref="ApplyCode"/>.</remarks>
public interface ICustomSettingsDefinition {
    /// <summary>
    /// Names the set: its files (<c>{FileKey}.json</c>, <c>{FileKey}.overrides.json</c>), its configuration
    /// section and its tab. Letters, digits, '-' and '_', unique among the sets registered. Changing it
    /// leaves the files written under the old one behind.
    /// </summary>
    string FileKey { get; }
    /// <summary>The class the settings are kept in: a public class with a public parameterless
    /// constructor, serialized with System.Text.Json.</summary>
    Type SettingsType { get; }
    /// <summary>Whether there is one set for the whole server, or one per database.</summary>
    CustomSettingsScope Scope { get; }
    /// <summary>The name of the tab in the admin UI, and of the set in messages: "Webshop".</summary>
    string Title { get; }
    /// <summary>Said at the top of the tab: what these settings are for. Optional.</summary>
    string? Description => null;
    /// <summary>A name the admin UI maps to a glyph for the tab and its section, as
    /// <see cref="SettingSectionDefinition.Icon"/> is ("server", "security", "storage", "messaging",
    /// "services"...). An unknown name gets a neutral settings icon.</summary>
    string Icon => "settings";
    /// <summary>
    /// What the tab shows: the settings in groups, each with the label and help text the page shows -
    /// <see cref="SettingDefinition.Path"/> is the property path into <see cref="SettingsType"/>
    /// ("Currency", "Shipping.FreeAbove"), and the editor, the choices of an enum and the default are read
    /// off the property itself. A property no group names is not editable on the tab. Left out, the tab
    /// shows every property in one group, labelled from <see cref="DisplayNameAttribute"/> and
    /// <see cref="DescriptionAttribute"/> where the class has them (<see cref="CustomSettingsCatalog.GroupsFor"/>).
    /// </summary>
    SettingGroupDefinition[] Groups => CustomSettingsCatalog.GroupsFor(SettingsType);
    /// <summary>Runs <see cref="ICustomSettingsDefinition{TSettings}.OnInit"/> on a settings object of
    /// <see cref="SettingsType"/>.</summary>
    void ApplyCode(object settings, NodeStoreContainerSettingsBase? database);
}

/// <summary>
/// A set of settings of type <typeparamref name="TSettings"/> registered by the application; see
/// <see cref="ICustomSettingsDefinition"/> for where the values come from. A definition is little more
/// than metadata:
/// <code>
/// public class WebshopSettings {
///     public string Currency { get; set; } = "NOK";
///     public decimal FreeShippingAbove { get; set; } = 500;
///     public string? PaymentApiKey { get; set; }
/// }
/// public class WebshopSettingsDefinition : ICustomSettingsDefinition&lt;WebshopSettings&gt; {
///     public string FileKey => "webshop";
///     public string Title => "Webshop";
///     public CustomSettingsScope Scope => CustomSettingsScope.Database;
///     public SettingGroupDefinition[] Groups => [
///         new() { Id = "checkout", Title = "Checkout", Settings = [
///             new() { Path = "Currency", Label = "Currency", Help = "What prices are shown in.", Applies = SettingApplies.Live },
///             new() { Path = "FreeShippingAbove", Label = "Free shipping above", Unit = "NOK", Help = "...", Applies = SettingApplies.Live },
///             new() { Path = "PaymentApiKey", Label = "Payment API key", Secret = true, Help = "...", Applies = SettingApplies.Live },
///         ] },
///     ];
/// }
/// // Program.cs
/// builder.AddRelatudeDB(o => o.AddCustomSettings(new WebshopSettingsDefinition()));
/// // anywhere a store is at hand
/// var shop = db.Settings.Get&lt;WebshopSettings&gt;();
/// </code>
/// </summary>
public interface ICustomSettingsDefinition<TSettings> : ICustomSettingsDefinition where TSettings : class, new() {
    Type ICustomSettingsDefinition.SettingsType => typeof(TSettings);
    /// <summary>
    /// The application's last word on the settings, run at every start after the files and the
    /// configuration section have been read: a value set here is shown on the tab as set by the code and
    /// locked, and never written to either file - the way <see cref="ServerOptions.OnServerSettingsInit"/>
    /// works for the server's own settings. <paramref name="database"/> is the database the set belongs
    /// to, null for a set of the server's. Does nothing unless implemented.
    /// </summary>
    void OnInit(TSettings settings, NodeStoreContainerSettingsBase? database) { }
    void ICustomSettingsDefinition.ApplyCode(object settings, NodeStoreContainerSettingsBase? database) => OnInit((TSettings)settings, database);
}

/// <summary>What the server checks a definition against, and the groups a definition gets when it names none.</summary>
public static class CustomSettingsCatalog {
    /// <summary>The configuration key below the RelatudeDB section that custom settings are read from.</summary>
    public const string ConfigurationKey = "CustomSettings";
    /// <summary>The key below <see cref="ConfigurationKey"/> that names one database's sets: <c>RelatudeDB:CustomSettings:Databases:{short name or id}:{FileKey}</c>.</summary>
    public const string DatabasesConfigurationKey = "Databases";

    static readonly Regex _fileKey = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled);
    // names a file key cannot have: a device name on Windows, the name of a file the settings folder keeps
    // already (datamodel.json), and the configuration key that names a database's sets
    static readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase) {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        Path.GetFileNameWithoutExtension(DatabaseShortName.DatamodelFileName), DatabasesConfigurationKey,
    };

    /// <summary>Why a file key cannot be used, or null when it can.</summary>
    public static string? FileKeyProblem(string? fileKey) {
        if (string.IsNullOrWhiteSpace(fileKey)) return "A set of custom settings needs a file key: it names its files.";
        if (!_fileKey.IsMatch(fileKey)) return "\"" + fileKey + "\" is not a file key: letters, digits, '-' and '_', starting with a letter or a digit, at most 64 characters. It names the set's files.";
        if (_reserved.Contains(fileKey)) return "\"" + fileKey + "\" cannot be a file key: the name is taken.";
        return null;
    }

    /// <summary>
    /// What is wrong with a definition, or an empty list: the file key, the settings type - one the
    /// server can make and serialize - and every path its groups name, which must be a property of the
    /// settings type. Run for every definition at start, so a mistake shows the first time the
    /// application is run rather than when the tab is opened.
    /// </summary>
    public static List<string> Problems(ICustomSettingsDefinition definition) {
        var problems = new List<string>();
        var name = "The custom settings \"" + definition.Title + "\" (" + definition.GetType().Name + ")";
        var keyProblem = FileKeyProblem(definition.FileKey);
        if (keyProblem != null) problems.Add(name + ": " + keyProblem);
        if (string.IsNullOrWhiteSpace(definition.Title)) problems.Add(name + " have no title: it names the tab.");
        var type = definition.SettingsType;
        if (!type.IsClass || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null) {
            problems.Add(name + ": " + type.FullName + " needs to be a class with a public parameterless constructor, so the server can make one.");
            return problems;
        }
        SettingGroupDefinition[] groups;
        try {
            groups = definition.Groups ?? [];
        } catch (Exception error) {
            problems.Add(name + ": the groups could not be read: " + error.Message);
            return problems;
        }
        foreach (var group in groups) {
            if (string.IsNullOrWhiteSpace(group.Id)) problems.Add(name + ": the group \"" + group.Title + "\" has no id.");
            foreach (var setting in group.Settings) check(setting.Path, type);
            if (group.List is { } list) {
                Type? elementType = null;
                try {
                    var collection = SettingsAccessor.Describe(type, list.Path).ValueType;
                    elementType = collection.IsArray ? collection.GetElementType() : null;
                    if (elementType == null) problems.Add(name + ": the list \"" + list.Path + "\" of the group \"" + group.Id + "\" is not an array.");
                    else if (elementType.GetProperty("Id")?.PropertyType != typeof(Guid)) problems.Add(name + ": the elements of \"" + list.Path + "\" need a Guid property named Id, which is how they are told apart.");
                } catch (Exception error) {
                    problems.Add(name + ": " + error.Message);
                }
                if (elementType != null) foreach (var field in list.Fields) check(field.Path, elementType);
            }
        }
        foreach (var duplicate in groups.GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) {
            problems.Add(name + ": the group id \"" + duplicate.Key + "\" is used twice.");
        }
        return problems;

        void check(string path, Type root) {
            try {
                SettingsAccessor.Describe(root, path);
            } catch (Exception error) {
                problems.Add(name + ": " + error.Message);
            }
        }
    }

    /// <summary>
    /// The groups of a definition that names none: one group, "Settings", with every public read-write
    /// property of the type that holds a single value - nested settings objects included, by their
    /// dotted path - labelled with <see cref="DisplayNameAttribute"/> where the property has one and its
    /// name in words where it does not, and explained with <see cref="DescriptionAttribute"/>. A
    /// property marked <c>[PasswordPropertyText(true)]</c> is a secret. Lists are left out: a
    /// list needs a <see cref="SettingListDefinition"/> to say what its elements are called.
    /// </summary>
    public static SettingGroupDefinition[] GroupsFor(Type settingsType) {
        var settings = SettingsAccessor.AllPaths(settingsType).Select(path => {
            var property = SettingsAccessor.Describe(settingsType, path).Property;
            var label = property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName;
            return new SettingDefinition {
                Path = path,
                Label = string.IsNullOrWhiteSpace(label) ? words(path) : label,
                Help = property.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "",
                Secret = property.GetCustomAttribute<PasswordPropertyTextAttribute>()?.Password == true,
                Applies = SettingApplies.Live,
            };
        }).ToArray();
        return [new SettingGroupDefinition { Id = "settings", Title = "Settings", Settings = settings }];
    }

    // "Shipping.FreeShippingAbove" -> "Shipping › Free shipping above", "PaymentAPIKey" -> "Payment API key"
    static string words(string path) => string.Join(" › ", path.Split('.').Select(part => {
        var split = Regex.Split(part, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])");
        return string.Join(" ", split.Select((word, i) => i == 0 || (word.Length > 1 && word.All(char.IsUpper)) ? word : word.ToLowerInvariant()));
    }));
}
