using System.Text.Json.Nodes;
using Relatude.DB.Datamodels;
using Relatude.DB.NodeServer;

namespace Relatude.Server;

/// <summary>
/// The two files a database's datamodel overrides are kept in: the shared one, deployed to every
/// installation, and the installation's, merged over it - a value there replaces the shared one, and null
/// takes the shared one away. What the editor activates is written as the difference from the shared file.
/// </summary>
[TestClass]
public class DatamodelOverridesLayersTests {
    static readonly Guid page = new("11111111-0000-0000-0000-000000000001");
    static readonly Guid news = new("11111111-0000-0000-0000-000000000002");
    static readonly Guid title = new("22222222-0000-0000-0000-000000000001");
    static readonly Guid body = new("22222222-0000-0000-0000-000000000002");

    static JsonObject shared() => DatamodelOverridesLayers.Parse($$"""
        // the application's own
        {
          "NodeTypes": {
            "{{page}}": {
              "Name": "Site.Page",
              "TextIndex": false,
              "Properties": {
                "{{title}}": { "Name": "Title", "DefaultValue": "Shared", "IndexBoost": 2 }
              }
            },
            "{{news}}": { "Name": "Site.News", "Hidden": true }
          }
        }
        """, keepResets: false)!;
    static JsonNode? value(JsonObject? layer, Guid type, Guid? property, string attribute) => DatamodelOverridesLayers.Get(layer, new(type, property, attribute), out _);
    static bool has(JsonObject? layer, Guid type, Guid? property, string attribute) {
        DatamodelOverridesLayers.Get(layer, new(type, property, attribute), out var found);
        return found;
    }

    [TestMethod]
    public void Merge_TheInstallationWins_AndNullTakesTheSharedValueAway() {
        var installation = DatamodelOverridesLayers.Parse($$"""
            { "NodeTypes": {
                "{{page}}": { "TextIndex": null, "Properties": { "{{title}}": { "DefaultValue": "Here" }, "{{body}}": { "Name": "Body", "ExcludeFromTextIndex": true } } },
                "{{news}}": { "Hidden": null }
            } }
            """, keepResets: true);
        var merged = DatamodelOverridesLayers.Merge(shared(), installation);
        Assert.IsFalse(has(merged, page, null, "TextIndex"), "taken away");
        Assert.AreEqual("Here", value(merged, page, title, "DefaultValue")!.GetValue<string>(), "replaced");
        Assert.AreEqual(2, value(merged, page, title, "IndexBoost")!.GetValue<int>(), "the shared values the installation says nothing about stay");
        Assert.AreEqual(true, value(merged, page, body, "ExcludeFromTextIndex")!.GetValue<bool>(), "added");
        Assert.IsNull(merged["NodeTypes"]![news.ToString()], "a type left with nothing goes");

        var typed = DatamodelOverridesLayers.ToOverrides(merged)!;
        Assert.AreEqual("Here", typed.NodeTypes[page].Properties![title].DefaultValue?.ToString());
        Assert.IsNull(typed.NodeTypes[page].TextIndex);
        Assert.IsFalse(typed.NodeTypes.ContainsKey(news));
    }

    [TestMethod]
    public void Diff_IsWhatDiffersFromTheShared_AndMergesBackIntoTheEffective() {
        var effective = shared().DeepClone().AsObject();
        // keep the title, change the boost, take Hidden away, add one
        effective["NodeTypes"]![page.ToString()]!["Properties"]![title.ToString()]!["IndexBoost"] = 5;
        effective["NodeTypes"]!.AsObject().Remove(news.ToString());
        effective["NodeTypes"]![page.ToString()]!["Properties"]!.AsObject()[body.ToString()] = new JsonObject { ["Name"] = "Body", ["DisplayName"] = true };
        var patch = DatamodelOverridesLayers.Diff(shared(), effective);
        var entries = DatamodelOverridesLayers.Entries(patch);
        Assert.AreEqual(3, entries.Count, string.Join(", ", entries.Select(e => e.Attribute)));
        Assert.AreEqual(5, value(patch, page, title, "IndexBoost")!.GetValue<int>());
        Assert.IsFalse(has(patch, page, title, "DefaultValue"), "what the shared file says comes from there");
        Assert.IsTrue(has(patch, news, null, "Hidden"));
        Assert.IsNull(value(patch, news, null, "Hidden"), "taken away: null");
        Assert.AreEqual("Site.News", entries.Single(e => e.TypeId == news).TypeName, "named for the person reading the file");
        Assert.IsTrue(has(patch, page, body, "DisplayName"));
        Assert.IsTrue(JsonNode.DeepEquals(effective, DatamodelOverridesLayers.Merge(shared(), patch)), "the patch over the shared file is the effective overrides");

        Assert.IsTrue(DatamodelOverridesLayers.IsEmpty(DatamodelOverridesLayers.Diff(shared(), shared())), "nothing differs, nothing to keep");
        Assert.AreEqual(4, DatamodelOverridesLayers.Entries(DatamodelOverridesLayers.Diff(null, shared())).Count, "without a shared file everything is the installation's");
    }

    [TestMethod]
    public void Parse_LinesUpFilesWrittenByHand() {
        var upper = page.ToString().ToUpperInvariant();
        var layer = DatamodelOverridesLayers.Parse($$"""
            { "nodeTypes": { "{{upper}}": { "textIndex": false, "hidden": null, "Typo": 1, "properties": { "{{title}}": { "defaultValue": "x", "indexBoost": null } } } } }
            """, keepResets: false)!;
        Assert.AreEqual(false, value(layer, page, null, "TextIndex")!.GetValue<bool>(), "the model's case, the usual id format");
        Assert.AreEqual("x", value(layer, page, title, "DefaultValue")!.GetValue<string>());
        Assert.IsFalse(has(layer, page, null, "Hidden"), "a null means nothing in the shared file");
        Assert.IsFalse(has(layer, page, title, "IndexBoost"));
        Assert.IsTrue(has(layer, page, null, "Typo"), "kept, for the model to report");
        var kept = DatamodelOverridesLayers.Parse("""{ "NodeTypes": { "%ID%": { "Hidden": null } } }""".Replace("%ID%", news.ToString()), keepResets: true)!;
        Assert.IsTrue(has(kept, news, null, "Hidden"), "in the installation's file it takes the shared value away");
        Assert.IsNull(DatamodelOverridesLayers.Parse("  ", keepResets: true));
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => DatamodelOverridesLayers.Parse("""{ "NodeTypes": { "Page": {} } }""", keepResets: false), "a type is named by its id");
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => DatamodelOverridesLayers.Parse("[]", keepResets: false));
    }

    [TestMethod]
    public void Move_WritesValuesIntoTheShared_ResetsTakeThemAway_AndWhatIsInForceStays() {
        var installation = DatamodelOverridesLayers.Parse($$"""
            { "NodeTypes": {
                "{{page}}": { "Name": "Site.Page", "TextIndex": null, "Properties": { "{{body}}": { "Name": "Body", "DisplayName": true } } },
                "{{news}}": { "Name": "Site.News", "SemanticIndex": true }
            } }
            """, keepResets: true);
        var before = DatamodelOverridesLayers.Merge(shared(), installation);
        var (newShared, rest, moved) = DatamodelOverridesLayers.Move(shared(), installation,
            [new(page, null, "TextIndex"), new(page, body, "DisplayName"), new(page, title, "DefaultValue")]);
        Assert.AreEqual(2, moved, "the title is not in the installation's file: passed over");
        Assert.IsFalse(has(newShared, page, null, "TextIndex"), "the reset took the shared value away");
        Assert.AreEqual(true, value(newShared, page, body, "DisplayName")!.GetValue<bool>());
        Assert.AreEqual("Body", newShared["NodeTypes"]![page.ToString()]!["Properties"]![body.ToString()]!["Name"]!.GetValue<string>(), "the name comes along");
        Assert.AreEqual(1, DatamodelOverridesLayers.Entries(rest).Count);
        Assert.IsTrue(has(rest, news, null, "SemanticIndex"));
        Assert.IsTrue(JsonNode.DeepEquals(before, DatamodelOverridesLayers.Merge(newShared, rest)), "nothing in force changes");
    }

    [TestMethod]
    public void FromOverrides_DropsWhatOverridesNothing() {
        var o = new DatamodelOverrides();
        o.NodeTypes[page] = new NodeTypeOverride { Name = "Site.Page", Properties = new() { [title] = new PropertyOverride { Name = "Title" } } };
        o.NodeTypes[news] = new NodeTypeOverride { Hidden = true };
        var layer = DatamodelOverridesLayers.FromOverrides(o);
        var entries = DatamodelOverridesLayers.Entries(layer);
        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(news, entries[0].TypeId);
        Assert.IsTrue(DatamodelOverridesLayers.IsEmpty(DatamodelOverridesLayers.FromOverrides(null)));
        Assert.IsNull(DatamodelOverridesLayers.ToOverrides(DatamodelOverridesLayers.FromOverrides(new DatamodelOverrides())));
    }
}
