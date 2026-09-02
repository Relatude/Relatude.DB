using Microsoft.Extensions.Configuration;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;
using System.Text.Json;
using System.Reflection;

namespace Relatude.Server;

/// <summary>
/// The admin UI's settings pages are generated from <see cref="SettingsCatalog"/>, and everything
/// under a definition's text - the editor, the choices, the default - is read off the settings class
/// by <see cref="SettingsAccessor"/>. That only holds while the paths in the catalog still name real
/// properties, so this fixture is what notices when a setting is renamed or removed.
/// </summary>
[TestClass]
public class SettingsCatalogTests {

    static IEnumerable<(SettingGroupDefinition Group, SettingDefinition Setting, Type Root)> all() {
        foreach (var (sections, root) in new (SettingSectionDefinition[], Type)[] {
            (SettingsCatalog.Server, typeof(RelatudeDBServerSettings)),
            (SettingsCatalog.Database, typeof(NodeStoreContainerSettings)),
        }) {
            foreach (var section in sections) {
                foreach (var group in section.Groups) {
                    foreach (var setting in group.Settings) yield return (group, setting, root);
                    // a list group's fields belong to one element, so they resolve against its type
                    if (group.List == null) continue;
                    var elementType = listProperty(root, group.List.Path).PropertyType.GetElementType()!;
                    foreach (var field in group.List.Fields) yield return (group, field, elementType);
                }
            }
        }
    }

    static IEnumerable<(SettingGroupDefinition Group, SettingListDefinition List, Type ElementType)> lists() {
        foreach (var (sections, root) in new (SettingSectionDefinition[], Type)[] {
            (SettingsCatalog.Server, typeof(RelatudeDBServerSettings)),
            (SettingsCatalog.Database, typeof(NodeStoreContainerSettings)),
        }) {
            foreach (var group in sections.SelectMany(s => s.Groups)) {
                if (group.List == null) continue;
                var property = listProperty(root, group.List.Path);
                Assert.IsTrue(property.PropertyType.IsArray, group.List.Path + " is not an array.");
                yield return (group, group.List, property.PropertyType.GetElementType()!);
            }
        }
    }

    [TestMethod]
    public void EverySettingResolvesAgainstItsSettingsClass() {
        foreach (var (group, setting, root) in all()) {
            var description = SettingsAccessor.Describe(root, setting.Path); // throws when the path is stale
            Assert.IsTrue(description.Property.GetMethod?.IsPublic == true, group.Id + "/" + setting.Path + " is not readable.");
            if (!setting.ReadOnly) {
                Assert.IsTrue(description.Property.SetMethod?.IsPublic == true, group.Id + "/" + setting.Path + " is not writable but is not marked read only.");
            }
        }
    }

    [TestMethod]
    public void EverySettingHasTextAndAUniquePath() {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>();
        foreach (var sections in new[] { SettingsCatalog.Server, SettingsCatalog.Database }) {
            paths.Clear();
            ids.Clear();
            foreach (var section in sections) {
                Assert.IsTrue(ids.Add("section:" + section.Id), "Duplicate section id: " + section.Id);
                Assert.IsFalse(string.IsNullOrWhiteSpace(section.Title), section.Id + " has no title.");
                // an unknown icon name falls back in the UI, but an empty one is a mistake here
                Assert.IsFalse(string.IsNullOrWhiteSpace(section.Icon), section.Id + " has no icon name.");
                Assert.IsTrue(section.Groups.Length > 0, section.Id + " has no groups.");
                foreach (var group in section.Groups) {
                    Assert.IsTrue(ids.Add("group:" + group.Id), "Duplicate group id: " + group.Id);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(group.Title), group.Id + " has no title.");
                    foreach (var setting in group.Settings) {
                        Assert.IsTrue(paths.Add(setting.Path), "Duplicate setting path: " + setting.Path);
                        Assert.IsFalse(string.IsNullOrWhiteSpace(setting.Label), setting.Path + " has no label.");
                        // the point of the catalog is the explanation, so an empty or token one is a bug
                        Assert.IsTrue(setting.Help.Length > 30, setting.Path + " has no real explanation.");
                    }
                    // a list group's field paths live in the element's own namespace, so they are
                    // checked among themselves rather than against the page. A path is allowed to
                    // repeat there; RepeatedListFieldsAreNeverVisibleTogether is what bounds that
                    if (group.List == null) continue;
                    var fieldPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var field in group.List.Fields) {
                        fieldPaths.Add(field.Path);
                        Assert.IsFalse(string.IsNullOrWhiteSpace(field.Label), field.Path + " has no label.");
                        Assert.IsTrue(field.Help.Length > 30, group.Id + "/" + field.Path + " has no real explanation.");
                    }
                    Assert.IsTrue(fieldPaths.Contains(group.List.LabelField), group.Id + " names a label field it does not have.");
                }
            }
        }
    }

    /// <summary>
    /// Not every setting has to be editable from the UI, but a new one going unnoticed is how the
    /// pages quietly fall behind. Anything added below has to be either listed in the catalog or
    /// named here, with the reason it is edited somewhere else.
    /// </summary>
    [TestMethod]
    public void NoSettingIsSilentlyMissingFromTheCatalog() {
        string[] editedElsewhere = [
            // server identity and the container list itself: the Databases section owns these
            "ContainerSettings", "Id",
            // the model source, storage provider, file store, url parent and url domain lists are
            // edited by their own list groups, whose element fields NoListFieldIsSilentlyMissing covers
            "DatamodelSources", "IOSettings", "FileStoreSettings",
            "LocalSettings.UrlOptions.Parents", "LocalSettings.UrlOptions.Domains",
            // the completion model map: a collection, not a single value
            "AISettings.CompletionModelsByKey",
            // what each log records and the query threshold: switched in the Logs section, which
            // writes them here with "Save and remember changes"
            "LocalSettings.LogRecording", "LocalSettings.MinQueryDurationMsBeforeLogging",
        ];
        var covered = SettingsCatalog.Server.Concat(SettingsCatalog.Database)
            .SelectMany(section => section.Groups).SelectMany(g => g.Settings).Select(s => s.Path)
            .Concat(editedElsewhere)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = SettingsAccessor.AllPaths(typeof(RelatudeDBServerSettings), maxDepth: 0)
            .Concat(SettingsAccessor.AllPaths(typeof(NodeStoreContainerSettings), maxDepth: 2))
            .Where(path => !covered.Contains(path))
            .Distinct()
            .ToArray();
        Assert.AreEqual(0, missing.Length, "Settings missing from SettingsCatalog: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void DefaultsComeFromTheSettingsClasses() {
        var cache = SettingsAccessor.Describe(typeof(NodeStoreContainerSettings), "LocalSettings.NodeCacheSizeGb");
        Assert.AreEqual(new SettingsLocal().NodeCacheSizeGb, cache.DefaultValue!.GetValue<double>());
        Assert.AreEqual(SettingEditor.Number, cache.Editor);

        var engine = SettingsAccessor.Describe(typeof(NodeStoreContainerSettings), "LocalSettings.PersistedQueueStoreEngine");
        Assert.AreEqual(SettingEditor.Choice, engine.Editor);
        CollectionAssert.AreEqual(Enum.GetNames<PersistedQueueStoreEngine>(), engine.EnumNames);
        Assert.AreEqual(new SettingsLocal().PersistedQueueStoreEngine.ToString(), engine.DefaultValue!.GetValue<string>());

        var autoOpen = SettingsAccessor.Describe(typeof(NodeStoreContainerSettings), "AutoOpen");
        Assert.AreEqual(SettingEditor.Toggle, autoOpen.Editor);
        Assert.IsFalse(autoOpen.Optional);

        // an optional reference is clearable, a non-optional one is not
        Assert.IsTrue(SettingsAccessor.Describe(typeof(RelatudeDBServerSettings), "Name").Optional);
        Assert.IsFalse(SettingsAccessor.Describe(typeof(RelatudeDBServerSettings), "TokenCookieName").Optional);
    }

    /// <summary>
    /// A catalog entry may name the values a free text setting usually holds. They are a shortcut in
    /// the UI and nothing more: the editor stays a text field, so anything else typed in its place
    /// still saves. A setting that really is closed belongs on an enum, whose members come from the
    /// type - which is why suggestions on one are a mistake rather than a combination.
    /// </summary>
    [TestMethod]
    public void SuggestedValuesStayFreeText() {
        var providerType = SettingsCatalog.Database.SelectMany(s => s.Groups).SelectMany(g => g.Settings)
            .Single(s => s.Path == "AISettings.TypeName");
        Assert.AreEqual(SettingEditor.Text, SettingsAccessor.Describe(typeof(NodeStoreContainerSettings), providerType.Path).Editor);
        CollectionAssert.Contains(providerType.Suggestions?.Select(s => s.Value).ToArray(), "OpenAI");

        foreach (var (group, setting, root) in all()) {
            if (setting.Suggestions == null) continue;
            var where = group.Id + "/" + setting.Path;
            var description = SettingsAccessor.Describe(root, setting.Path);
            Assert.AreEqual(SettingEditor.Text, description.Editor, where + " suggests values but is not a text setting.");
            Assert.IsTrue(setting.Suggestions.Length > 0, where + " carries an empty suggestion list.");
            Assert.IsFalse(setting.Suggestions.Any(s => string.IsNullOrWhiteSpace(s.Value)), where + " suggests a blank value.");
            Assert.AreEqual(setting.Suggestions.Length, setting.Suggestions.Select(s => s.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                where + " suggests the same value twice.");
        }
    }

    [TestMethod]
    public void WritingFollowsThePathAndCreatesMissingObjects() {
        var container = new NodeStoreContainerSettings { Id = Guid.NewGuid() };
        Assert.IsNull(container.LocalSettings);
        Assert.IsTrue(SettingsAccessor.Write(container, "LocalSettings.NodeCacheSizeGb", json("2.5")));
        Assert.AreEqual(2.5, container.LocalSettings!.NodeCacheSizeGb);

        // enums arrive as their names, numbers may arrive as strings from a number input
        Assert.IsTrue(SettingsAccessor.Write(container, "LocalSettings.PersistedQueueStoreEngine", json("\"Sqlite\"")));
        Assert.AreEqual(PersistedQueueStoreEngine.Sqlite, container.LocalSettings.PersistedQueueStoreEngine);
        Assert.IsTrue(SettingsAccessor.Write(container, "LocalSettings.ImageDefaultQuality", json("\"70\"")));
        Assert.AreEqual(70, container.LocalSettings.ImageDefaultQuality);

        // writing the value it already holds is not a change, so a save reports nothing happened
        Assert.IsFalse(SettingsAccessor.Write(container, "LocalSettings.ImageDefaultQuality", json("70")));

        // clearing: an optional value goes back to null, a required one to its zero value
        Assert.IsTrue(SettingsAccessor.Write(container, "IoBackup", json("\"" + Guid.NewGuid() + "\"")));
        Assert.IsTrue(SettingsAccessor.Write(container, "IoBackup", json("\"\"")));
        Assert.IsNull(container.IoBackup);

        var server = new RelatudeDBServerSettings();
        Assert.IsTrue(SettingsAccessor.Write(server, "TokenCookieName", json("\"\"")));
        Assert.AreEqual("", server.TokenCookieName); // not null: the property is not nullable
    }

    [TestMethod]
    public void ReadOnlySettingsAreRefused() {
        var server = new RelatudeDBServerSettings();
        Assert.ThrowsException<Exception>(() => SettingsAccessor.Write(server, "NotASetting", json("1")));
    }

    /// <summary>
    /// A setting the configuration section decides must be reported as such, per database as well as
    /// per server, so the UI can lock it instead of offering an edit that would be undone.
    /// </summary>
    [TestMethod]
    public void OverriddenSettingsAreReportedOnTheirFullPath() {
        var file = RelatudeDBServerSettings.CreateDefault();
        var containerId = file.ContainerSettings![0].Id;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["RelatudeDB:MasterPassword"] = "from-configuration",
            ["RelatudeDB:ContainerSettings:0:Id"] = containerId.ToString(),
            ["RelatudeDB:ContainerSettings:0:LocalSettings:NodeCacheSizeGb"] = "8",
        }).Build();
        var overlay = SettingsOverlay.Create(configuration, SettingsOverlay.DefaultSectionName, _ => { }, _ => { })!;
        overlay.Apply(file);

        Assert.IsTrue(overlay.IsOverridden(SettingsOverlay.OverridePath(null, "MasterPassword"), out var master));
        Assert.AreEqual("from-configuration", master!.GetValue<string>());
        Assert.IsTrue(overlay.IsOverridden(SettingsOverlay.OverridePath(containerId, "LocalSettings.NodeCacheSizeGb"), out var cache));
        Assert.AreEqual(8d, cache!.GetValue<double>());

        Assert.IsFalse(overlay.IsOverridden(SettingsOverlay.OverridePath(null, "MasterUserName"), out _));
        Assert.IsFalse(overlay.IsOverridden(SettingsOverlay.OverridePath(containerId, "LocalSettings.SetCacheSizeGb"), out _));
        Assert.IsFalse(overlay.IsOverridden(SettingsOverlay.OverridePath(Guid.NewGuid(), "LocalSettings.NodeCacheSizeGb"), out _));
    }

    /// <summary>The same drift guard as for the pages, one level down: a property added to a storage
    /// provider or a file store has to show up in its list group or be named here.</summary>
    [TestMethod]
    public void NoListFieldIsSilentlyMissing() {
        foreach (var (group, list, elementType) in lists()) {
            var covered = list.Fields.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = SettingsAccessor.AllPaths(elementType, maxDepth: 0).Where(p => !covered.Contains(p)).ToArray();
            Assert.AreEqual(0, missing.Length, group.Id + " is missing fields: " + string.Join(", ", missing));
        }
    }

    [TestMethod]
    public void ListFieldVisibilityNamesASiblingAndRealValues() {
        foreach (var (group, list, elementType) in lists()) {
            foreach (var field in list.Fields) {
                if (field.VisibleWhen == null) continue;
                var sibling = list.Fields.FirstOrDefault(f => string.Equals(f.Path, field.VisibleWhen.Path, StringComparison.OrdinalIgnoreCase));
                Assert.IsNotNull(sibling, group.Id + "/" + field.Path + " depends on \"" + field.VisibleWhen.Path + "\", which is not a field.");
                var type = SettingsAccessor.Describe(elementType, sibling!.Path).ValueType;
                Assert.IsTrue(type.IsEnum, group.Id + "/" + field.Path + " depends on a field that is not a fixed set of values.");
                foreach (var value in field.VisibleWhen.Values) {
                    Assert.IsTrue(Enum.GetNames(type).Contains(value), value + " is not a " + type.Name + ".");
                }
            }
        }
    }

    /// <summary>
    /// One property may appear as more than one field - a model source's Reference names an assembly,
    /// a type or a file depending on its kind - so each kind can label and explain it as its own. That
    /// only holds while at most one copy is ever shown: two visible at once would render the same
    /// value twice, and the second would shadow the first in every map keyed by path.
    /// </summary>
    [TestMethod]
    public void RepeatedListFieldsAreNeverVisibleTogether() {
        var repeated = 0;
        foreach (var (group, list, _) in lists()) {
            foreach (var same in list.Fields.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) {
                var copies = same.ToArray();
                repeated++;
                foreach (var copy in copies) {
                    Assert.IsNotNull(copy.VisibleWhen, group.Id + "/" + same.Key + " appears more than once but one copy is always visible.");
                }
                for (var i = 0; i < copies.Length; i++) {
                    for (var j = i + 1; j < copies.Length; j++) {
                        var a = copies[i].VisibleWhen!;
                        var b = copies[j].VisibleWhen!;
                        Assert.IsTrue(StringComparer.OrdinalIgnoreCase.Equals(a.Path, b.Path),
                            group.Id + "/" + same.Key + " has copies depending on different fields, so nothing keeps them apart.");
                        var overlap = a.Values.Intersect(b.Values, StringComparer.OrdinalIgnoreCase).ToArray();
                        Assert.AreEqual(0, overlap.Length, group.Id + "/" + same.Key + " shows two copies at once for " + string.Join(", ", overlap) + ".");
                    }
                }
            }
        }
        Assert.IsTrue(repeated > 0, "No field repeats any more - the test no longer covers anything.");
    }

    /// <summary>A choice the settings file may not hold is not offered at all. The datamodel source
    /// type Code is the one: it belongs to model types registered from application code, and the
    /// loader refuses a configured source carrying it.</summary>
    [TestMethod]
    public void ExcludedChoicesNameRealEnumMembers() {
        var found = 0;
        foreach (var (sections, root) in new (SettingSectionDefinition[], Type)[] {
            (SettingsCatalog.Server, typeof(RelatudeDBServerSettings)),
            (SettingsCatalog.Database, typeof(NodeStoreContainerSettings)),
        }) {
            foreach (var group in sections.SelectMany(s => s.Groups)) {
                var elementType = group.List == null ? null : listProperty(root, group.List.Path).PropertyType.GetElementType()!;
                foreach (var (owner, field) in group.Settings.Select(f => (root, f)).Concat((group.List?.Fields ?? []).Select(f => (elementType!, f)))) {
                    if (field.ExcludedChoices == null) continue;
                    var type = SettingsAccessor.Describe(owner, field.Path).ValueType;
                    Assert.IsTrue(type.IsEnum, group.Id + "/" + field.Path + " excludes choices but is not a fixed set of values.");
                    foreach (var name in field.ExcludedChoices) {
                        Assert.IsTrue(Enum.GetNames(type).Contains(name), name + " is not a " + type.Name + ".");
                        found++;
                    }
                }
            }
        }
        Assert.IsTrue(found > 0, "Nothing excludes a choice any more - the test no longer covers anything.");
    }

    /// <summary>A model source added from the admin UI starts turned off, so the settings file is
    /// never left holding a half configured source - which, unlike a half configured storage provider,
    /// nothing has to point at to break: every source is loaded, and one that fails stops the open.</summary>
    [TestMethod]
    public void ANewModelSourceStartsTurnedOff() {
        var list = SettingsCatalog.Database.SelectMany(s => s.Groups).Select(g => g.List)
            .Single(l => l?.Path == "DatamodelSources")!;
        CollectionAssert.Contains(list.Fields.First(f => f.Path == "Type").ExcludedChoices, nameof(DatamodelSourceType.Code));
        Assert.IsTrue(new DatamodelSource().Enabled, "A source in a settings file written before the flag existed has to keep loading.");

        // the seeded values are written through the same conversion as any edit, booleans included
        var source = new DatamodelSource { Id = Guid.NewGuid() };
        var container = new NodeStoreContainerSettings { Id = Guid.NewGuid(), DatamodelSources = [source] };
        var prefix = "DatamodelSources[" + source.Id + "].";
        foreach (var (field, value) in list.NewItem!) {
            SettingsAccessor.Write(container, prefix + field, JsonSerializer.SerializeToElement(value));
        }
        Assert.IsFalse(source.Enabled);
        Assert.AreEqual(DatamodelSourceType.AssemblyNameReference, source.Type);
        Assert.AreEqual("New model source", source.Name);
    }

    /// <summary>An element's fields are reached on a path that addresses the element by Id, which is
    /// what lets them save, show defaults and report overrides like any other setting.</summary>
    [TestMethod]
    public void ElementsAreReadAndWrittenById() {
        var io = new IOSettings { Id = Guid.NewGuid(), Name = "Disk", IOType = IOTypes.LocalDisk, Path = "old" };
        var other = new IOSettings { Id = Guid.NewGuid(), Name = "Blob", IOType = IOTypes.AzureBlobStorage };
        var container = new NodeStoreContainerSettings { Id = Guid.NewGuid(), IOSettings = [io, other] };

        var path = "IOSettings[" + io.Id + "].Path";
        Assert.AreEqual("old", SettingsAccessor.Read(container, path)!.GetValue<string>());
        Assert.IsTrue(SettingsAccessor.Write(container, path, json("\"new\"")));
        Assert.AreEqual("new", io.Path);
        Assert.IsNull(other.Path); // the id picks one element, not a position

        var described = SettingsAccessor.Describe(typeof(NodeStoreContainerSettings), "IOSettings[" + io.Id + "].IOType");
        Assert.AreEqual(SettingEditor.Choice, described.Editor);
        CollectionAssert.AreEqual(Enum.GetNames<IOTypes>(), described.EnumNames);

        // reordering the array does not change what a path means
        container.IOSettings = [other, io];
        Assert.AreEqual("new", SettingsAccessor.Read(container, path)!.GetValue<string>());

        // a path into an element that is gone is a stale request, not an empty value
        container.IOSettings = [other];
        Assert.ThrowsException<Exception>(() => SettingsAccessor.Read(container, path));
        Assert.ThrowsException<Exception>(() => SettingsAccessor.Write(container, path, json("\"x\"")));
    }

    /// <summary>Configuration reaches into an element the same way, so a provider whose folder comes
    /// from appsettings is reported as locked rather than offered for editing.</summary>
    [TestMethod]
    public void ConfigurationOverridesReachIntoListElements() {
        var file = RelatudeDBServerSettings.CreateDefault();
        var container = file.ContainerSettings![0];
        var ioId = container.IOSettings![0].Id;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["RelatudeDB:ContainerSettings:0:Id"] = container.Id.ToString(),
            ["RelatudeDB:ContainerSettings:0:IOSettings:0:Id"] = ioId.ToString(),
            ["RelatudeDB:ContainerSettings:0:IOSettings:0:Path"] = "from-configuration",
        }).Build();
        var overlay = SettingsOverlay.Create(configuration, SettingsOverlay.DefaultSectionName, _ => { }, _ => { })!;
        overlay.Apply(file);

        Assert.IsTrue(overlay.IsOverridden(SettingsOverlay.OverridePath(container.Id, "IOSettings[" + ioId + "].Path"), out var folder));
        Assert.AreEqual("from-configuration", folder!.GetValue<string>());
        Assert.IsFalse(overlay.IsOverridden(SettingsOverlay.OverridePath(container.Id, "IOSettings[" + ioId + "].Name"), out _));
    }

    /// <summary>A generated value only makes sense in a free text field that holds a random key or
    /// secret: a choice, a picker or a number has nothing random to offer.</summary>
    [TestMethod]
    public void GeneratedValuesBelongToFreeTextSettings() {
        var found = 0;
        foreach (var (group, setting, root) in all()) {
            if (setting.Generate == null) continue;
            found++;
            var where = group.Id + "/" + setting.Path;
            Assert.AreEqual(SettingGenerators.Guid, setting.Generate, where + " asks for a kind of generated value the page cannot make.");
            var description = SettingsAccessor.Describe(root, setting.Path);
            Assert.AreEqual(SettingEditor.Text, description.Editor, where + " offers a generated value but is not a text setting.");
            Assert.IsNull(setting.Picker, where + " offers a generated value but picks from a list.");
            Assert.IsNull(setting.Suggestions, where + " offers a generated value and suggestions at once.");
            Assert.IsFalse(setting.ReadOnly, where + " offers a generated value but cannot be edited.");
        }
        Assert.IsTrue(found >= 2, "The token secret and the file URL signing key should offer a generated value; found " + found + ".");
    }

    // a list may sit on a nested settings object ("LocalSettings.ValueIndexes"), so its path is walked
    static PropertyInfo listProperty(Type root, string path) {
        var type = root;
        PropertyInfo? property = null;
        foreach (var segment in path.Split('.')) {
            property = type.GetProperty(segment);
            Assert.IsNotNull(property, "\"" + path + "\" does not exist: " + type.Name + " has no " + segment + ".");
            type = property!.PropertyType;
        }
        return property!;
    }

    /// <summary>
    /// The engines a default can point at are edited in three list groups below the defaults, one per
    /// index kind. Each offers the built-in engines of its kind as suggestions - the same names
    /// <c>IndexEngineTypes</c> knows, and LateBindingsTests checks they all resolve - and starts a new
    /// engine on one of them, so an added engine is usable as added.
    /// </summary>
    [TestMethod]
    public void IndexEngineListsOfferTheBuiltInEnginesOfTheirKind() {
        var lists = SettingsCatalog.Database.SelectMany(s => s.Groups).Select(g => g.List).OfType<SettingListDefinition>().ToArray();
        foreach (var (path, known, defaultSetting) in new[] {
            ("LocalSettings.ValueIndexes", IndexEngineTypes.ValueEngines, "LocalSettings.DefaultValueIndex"),
            ("LocalSettings.TextIndexes", IndexEngineTypes.TextEngines, "LocalSettings.DefaultTextIndex"),
            ("LocalSettings.VectorIndexes", IndexEngineTypes.VectorEngines, "LocalSettings.DefaultVectorIndex"),
        }) {
            var list = lists.SingleOrDefault(l => l.Path == path);
            Assert.IsNotNull(list, path + " has no list group.");
            var type = list!.Fields.Single(f => f.Path == "TypeName");
            CollectionAssert.AreEquivalent(known, type.Suggestions!.Select(s => s.Value).ToArray(), path + " suggests other engines than IndexEngineTypes knows.");
            Assert.IsTrue(list.Fields.Single(f => f.Path == "Id").ReadOnly, path + ": the id must not be editable, the default and the folder refer to it.");
            CollectionAssert.Contains(known, list.NewItem!["TypeName"], path + ": a new engine must start on a built-in one.");
            // the default that chooses among the list's engines is a picker, so Guid.Empty is offered as "Memory"
            var setting = SettingsCatalog.Database.SelectMany(s => s.Groups).SelectMany(g => g.Settings).Single(s => s.Path == defaultSetting);
            Assert.IsFalse(string.IsNullOrEmpty(setting.Picker), defaultSetting + " should pick from the configured engines.");
        }
    }

    /// <summary>The settings page reaches an engine's fields through the nested LocalSettings object
    /// and the element's id, the same way it reaches a top level list's elements.</summary>
    [TestMethod]
    public void IndexEngineElementsAreReachedThroughLocalSettings() {
        var engine = new IndexEngineSettings { Id = Guid.NewGuid(), TypeName = IndexEngineTypes.Native };
        var container = new NodeStoreContainerSettings { Id = Guid.NewGuid(), LocalSettings = new SettingsLocal { ValueIndexes = [engine] } };
        var path = "LocalSettings.ValueIndexes[" + engine.Id + "].MaxMemoryUsageInMb";
        Assert.AreEqual(new IndexEngineSettings().MaxMemoryUsageInMb, SettingsAccessor.Read(container, path)!.GetValue<int>());
        Assert.IsTrue(SettingsAccessor.Write(container, path, json("\"64\"")));
        Assert.AreEqual(64, engine.MaxMemoryUsageInMb);
        var described = SettingsAccessor.Describe(typeof(NodeStoreContainerSettings), "LocalSettings.ValueIndexes[" + engine.Id + "].TypeName");
        Assert.AreEqual(SettingEditor.Text, described.Editor); // free text: a custom engine's type name saves too
        Assert.IsTrue(SettingsAccessor.Describe(typeof(NodeStoreContainerSettings), "LocalSettings.ValueIndexes[" + engine.Id + "].Id").Property.CanRead);
    }

    static JsonElement json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
}
