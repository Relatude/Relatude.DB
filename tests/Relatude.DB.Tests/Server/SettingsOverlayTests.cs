using Microsoft.Extensions.Configuration;
using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.Server;

/// <summary>
/// The RelatudeDB configuration section overrides relatude.db.json: any key, coerced to the setting's
/// type, arrays matched on Id or position, unknown keys warned about, and every override stripped
/// again before the settings are written back - a secret placed in appsettings or an environment
/// variable must never end up in relatude.db.json.
/// </summary>
[TestClass]
public class SettingsOverlayTests {

    static RelatudeDBServerSettings fileSettings() {
        var settings = RelatudeDBServerSettings.CreateDefault();
        settings.MasterUserName = "fileuser";
        settings.MasterPassword = "filepassword";
        return settings;
    }
    static (SettingsOverlay? Overlay, List<string> Info, List<string> Warnings) create(Dictionary<string, string?> values) {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var info = new List<string>();
        var warnings = new List<string>();
        var overlay = SettingsOverlay.Create(configuration, SettingsOverlay.DefaultSectionName, info.Add, warnings.Add);
        return (overlay, info, warnings);
    }

    [TestMethod]
    public void AbsentSection_YieldsNoOverlay() {
        var (overlay, _, warnings) = create([]);
        Assert.IsNull(overlay);
        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    public void ScalarOverride_AppliesAndKeepsTheRest() {
        var (overlay, info, warnings) = create(new() { ["RelatudeDB:MasterUserName"] = "configuser" });
        var file = fileSettings();
        var applied = overlay!.Apply(file);
        Assert.AreEqual("configuser", applied.MasterUserName);
        Assert.AreEqual("filepassword", applied.MasterPassword);
        Assert.AreEqual(file.DefaultStoreId, applied.DefaultStoreId);
        Assert.AreEqual(1, applied.ContainerSettings!.Length);
        Assert.IsTrue(overlay.HasOverrides);
        Assert.AreEqual(1, info.Count);
        StringAssert.Contains(info[0], "MasterUserName");
        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    public void Keys_AreCaseInsensitive() {
        var (overlay, _, warnings) = create(new() { ["RelatudeDB:masterusername"] = "configuser" });
        var applied = overlay!.Apply(fileSettings());
        Assert.AreEqual("configuser", applied.MasterUserName);
        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    public void Values_AreCoercedToTheSettingType() {
        var (overlay, _, warnings) = create(new() {
            ["RelatudeDB:ContainerSettings:0:LocalSettings:AutoBackUp"] = "true",
            ["RelatudeDB:ContainerSettings:0:LocalSettings:NodeCacheSizeGb"] = "2.5",
            ["RelatudeDB:ContainerSettings:0:LocalSettings:NoHourlyBackUps"] = "3",
            ["RelatudeDB:ContainerSettings:0:LocalSettings:DefaultFileStoreEngine"] = "singlefile",
        });
        var applied = overlay!.Apply(fileSettings());
        var local = applied.ContainerSettings![0].LocalSettings!;
        Assert.IsTrue(local.AutoBackUp);
        Assert.AreEqual(2.5, local.NodeCacheSizeGb);
        Assert.AreEqual(3, local.NoHourlyBackUps);
        Assert.AreEqual(DB.DataStores.FileStoreEngine.SingleFile, local.DefaultFileStoreEngine);
        Assert.AreEqual(0, warnings.Count);
    }

    [TestMethod]
    public void SameValueAsTheFile_IsNotAnOverride() {
        var (overlay, info, _) = create(new() { ["RelatudeDB:MasterUserName"] = "fileuser" });
        var file = fileSettings();
        var applied = overlay!.Apply(file);
        Assert.AreSame(file, applied);
        Assert.IsFalse(overlay.HasOverrides);
        Assert.AreEqual(0, info.Count);
    }

    [TestMethod]
    public void ArrayElement_IsMatchedOnId() {
        var file = fileSettings();
        var second = new NodeStoreContainerSettings { Id = Guid.NewGuid(), Name = "Second" };
        file.ContainerSettings = [file.ContainerSettings![0], second];
        var (overlay, _, warnings) = create(new() {
            ["RelatudeDB:ContainerSettings:0:Id"] = second.Id.ToString(),
            ["RelatudeDB:ContainerSettings:0:Name"] = "Patched",
        });
        var applied = overlay!.Apply(file);
        Assert.AreEqual("MyDatabase", applied.ContainerSettings![0].Name);
        Assert.AreEqual("Patched", applied.ContainerSettings[1].Name);
        Assert.AreEqual(0, warnings.Count, "matching an element by its own Id is not an identity change");
    }

    [TestMethod]
    public void ArrayElement_WithoutId_IsMatchedOnPosition() {
        var (overlay, _, _) = create(new() { ["RelatudeDB:ContainerSettings:0:Name"] = "ByPosition" });
        var applied = overlay!.Apply(fileSettings());
        Assert.AreEqual("ByPosition", applied.ContainerSettings![0].Name);
    }

    [TestMethod]
    public void ArrayElement_WithUnknownId_IsAppended() {
        var (overlay, _, _) = create(new() {
            ["RelatudeDB:ContainerSettings:1:Id"] = Guid.NewGuid().ToString(),
            ["RelatudeDB:ContainerSettings:1:Name"] = "Extra",
        });
        var applied = overlay!.Apply(fileSettings());
        Assert.AreEqual(2, applied.ContainerSettings!.Length);
        Assert.AreEqual("Extra", applied.ContainerSettings[1].Name);
    }

    [TestMethod]
    public void UnknownKey_WarnsAndIsIgnored() {
        var (overlay, _, warnings) = create(new() {
            ["RelatudeDB:MasterPasword"] = "typo",
            ["RelatudeDB:MasterUserName"] = "configuser",
        });
        var applied = overlay!.Apply(fileSettings());
        Assert.AreEqual("configuser", applied.MasterUserName);
        Assert.AreEqual("filepassword", applied.MasterPassword);
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "MasterPasword");
    }

    [TestMethod]
    public void OnlyUnknownKeys_YieldsNoOverlayButWarns() {
        var (overlay, _, warnings) = create(new() { ["RelatudeDB:NotASetting"] = "x" });
        Assert.IsNull(overlay);
        Assert.AreEqual(1, warnings.Count);
    }

    [TestMethod]
    public void InvalidValue_WarnsAndIsIgnored() {
        var (overlay, _, warnings) = create(new() {
            ["RelatudeDB:TokenCookieMaxAgeInSec"] = "notanumber",
            ["RelatudeDB:MasterUserName"] = "configuser",
        });
        var file = fileSettings();
        var applied = overlay!.Apply(file);
        Assert.AreEqual(file.TokenCookieMaxAgeInSec, applied.TokenCookieMaxAgeInSec);
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "TokenCookieMaxAgeInSec");
    }

    [TestMethod]
    public void AiSettingsOverride_AppliesPerContainerAndIsStrippedOnSave() {
        var file = fileSettings();
        file.ContainerSettings![0].AISettings = new AIProviderSettings { IndexType = AIIndexType.Memory };
        var (overlay, _, warnings) = create(new() {
            ["RelatudeDB:ContainerSettings:0:AISettings:ApiKey"] = "configsecret",
            ["RelatudeDB:ContainerSettings:0:AISettings:IndexType"] = "HNSW",
            ["RelatudeDB:ContainerSettings:0:AISettings:IndexCacheSizeInMb"] = "64",
        });
        var applied = overlay!.Apply(file);
        var ai = applied.ContainerSettings![0].AISettings!;
        Assert.AreEqual("configsecret", ai.ApiKey);
        Assert.AreEqual(AIIndexType.HNSW, ai.IndexType);
        Assert.AreEqual(64d, ai.IndexCacheSizeInMb);
        Assert.AreEqual(0, warnings.Count);

        var stripped = overlay.RemoveOverridesBeforeSave(applied);
        var strippedAi = stripped.ContainerSettings![0].AISettings!;
        Assert.IsNull(strippedAi.ApiKey, "a configuration-supplied secret must not reach the file");
        Assert.AreEqual(AIIndexType.Memory, strippedAi.IndexType);
        Assert.IsNull(strippedAi.IndexCacheSizeInMb);
    }

    [TestMethod]
    public void IdentityKeyOverride_Warns() {
        var (overlay, _, warnings) = create(new() { ["RelatudeDB:DefaultStoreId"] = Guid.NewGuid().ToString() });
        overlay!.Apply(fileSettings());
        Assert.AreEqual(1, warnings.Count);
        StringAssert.Contains(warnings[0], "identity");
    }

    [TestMethod]
    public void RemoveOverridesBeforeSave_RestoresTheFileValues() {
        var (overlay, _, _) = create(new() {
            ["RelatudeDB:MasterPassword"] = "configsecret",
            ["RelatudeDB:Description"] = "dev only",
            ["RelatudeDB:ContainerSettings:0:LocalSettings:AutoBackUp"] = "true",
            ["RelatudeDB:ContainerSettings:1:Id"] = Guid.NewGuid().ToString(),
            ["RelatudeDB:ContainerSettings:1:Name"] = "Extra",
        });
        var live = overlay!.Apply(fileSettings());
        Assert.AreEqual("configsecret", live.MasterPassword);
        Assert.AreEqual(2, live.ContainerSettings!.Length);

        live.Name = "EditedByAdmin"; // an admin UI change to a key the overlay does not touch
        var stripped = overlay.RemoveOverridesBeforeSave(live);

        Assert.AreEqual("filepassword", stripped.MasterPassword, "a configuration-supplied secret must not reach the file");
        Assert.IsNull(stripped.Description);
        Assert.IsFalse(stripped.ContainerSettings![0].LocalSettings!.AutoBackUp);
        Assert.AreEqual(1, stripped.ContainerSettings.Length, "an appended element is removed again");
        Assert.AreEqual("EditedByAdmin", stripped.Name, "changes to keys the overlay does not touch are kept");
        Assert.AreEqual("configsecret", live.MasterPassword, "the live settings keep running on the overridden values");
    }

    [TestMethod]
    public void RemoveOverridesBeforeSave_FindsElementsById_AfterReordering() {
        var file = fileSettings();
        var second = new NodeStoreContainerSettings {
            Id = Guid.NewGuid(),
            Name = "Second",
            LocalSettings = new DB.DataStores.SettingsLocal(),
        };
        file.ContainerSettings = [file.ContainerSettings![0], second];
        var (overlay, _, _) = create(new() {
            ["RelatudeDB:ContainerSettings:0:Id"] = second.Id.ToString(),
            ["RelatudeDB:ContainerSettings:0:LocalSettings:AutoBackUp"] = "true",
        });
        var live = overlay!.Apply(file);
        Assert.IsTrue(live.ContainerSettings![1].LocalSettings!.AutoBackUp);

        live.ContainerSettings = [live.ContainerSettings[1], live.ContainerSettings[0]]; // admin reorders
        var stripped = overlay.RemoveOverridesBeforeSave(live);

        var strippedSecond = stripped.ContainerSettings!.Single(c => c.Id == second.Id);
        Assert.IsFalse(strippedSecond.LocalSettings!.AutoBackUp, "the override is stripped from the right element after reordering");
    }
}
