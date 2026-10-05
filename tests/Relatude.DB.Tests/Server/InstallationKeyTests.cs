using System.Text.Json;
using Relatude.DB.Common;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.Server;

/// <summary>
/// The key Relatude Services knows an installation by: server id, host id and an id kept with the
/// default database. One relatude.db.json published to two applications on one host has to give two
/// installations, and an application has to keep its key across restarts and Azure worker moves.
/// </summary>
[TestClass]
public class InstallationKeyTests {
    readonly List<string> _roots = [];

    string newRoot() {
        var root = Path.Combine(Path.GetTempPath(), "relatude.installation." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    [TestCleanup]
    public void Cleanup() {
        foreach (var root in _roots) {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // where a server whose default database keeps nothing (memory storage) keeps the id
    static string fallbackFile(string root) => Path.Combine(root, "relatude.db", "installation", "installation.json");

    static string[] partsOf(string key) => key.Split(':');

    [TestMethod]
    public async Task TwoApplicationsWithTheSameSettings_OnOneHost_GetTwoKeys() {
        var serverId = Guid.NewGuid();
        var first = TestServerHost.Start(newRoot(), configure: s => s.Id = serverId);
        var second = TestServerHost.Start(newRoot(), configure: s => s.Id = serverId);
        try {
            var a = first.Server.LicenseLogin.DescribeInstallation();
            var b = second.Server.LicenseLogin.DescribeInstallation();
            Assert.AreNotEqual(a.Key, b.Key, "each application keeps its own data, and so its own id");
            Assert.AreEqual(serverId, a.ServerId);
            Assert.AreEqual(serverId, b.ServerId);
            Assert.AreEqual(a.HostId, b.HostId, "one host");
            Assert.AreNotEqual(a.DataId, b.DataId);
            CollectionAssert.AreEqual(new[] { serverId.ToString("D"), a.HostId, a.DataId }, partsOf(a.Key), "server id:host id:data id");
        } finally {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task TheKey_IsTheSameAfterARestart() {
        var root = newRoot();
        var serverId = Guid.NewGuid();
        var host = TestServerHost.Start(root, configure: s => s.Id = serverId);
        string key;
        try {
            key = host.Server.LicenseLogin.DescribeInstallation().Key;
            Assert.IsTrue(File.Exists(fallbackFile(root)), "a database in memory keeps nothing, so the id is kept below the root data folder");
            Assert.AreEqual("relatude.db/installation/installation.json", host.Server.LicenseLogin.DescribeInstallation().DataIdPlace);
        } finally {
            await host.DisposeAsync();
        }
        var again = TestServerHost.Start(root, configure: s => s.Id = serverId);
        try {
            Assert.AreEqual(key, again.Server.LicenseLogin.DescribeInstallation().Key);
        } finally {
            await again.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task TheId_IsKeptWithTheDefaultDatabase() {
        var root = newRoot();
        var host = TestServerHost.Start(root, settings: diskSettings("first", "second"));
        try {
            var installation = host.Server.LicenseLogin.DescribeInstallation();
            Assert.IsNull(installation.DataIdProblem, installation.DataIdProblem);
            Assert.AreEqual("second/installation/installation.json", installation.DataIdPlace, "the default database is the second one");
            var file = Path.Combine(root, "second", "installation", "installation.json");
            var written = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            Assert.AreEqual(installation.DataId, Guid.Parse(written.GetProperty("Id").GetString()!).ToString("N"));
            Assert.IsFalse(File.Exists(fallbackFile(root)));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "first", "installation")));
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AFileBrokenByHand_IsReplacedWithANewId() {
        var root = newRoot();
        Directory.CreateDirectory(Path.GetDirectoryName(fallbackFile(root))!);
        File.WriteAllText(fallbackFile(root), "not json");
        var host = TestServerHost.Start(root);
        try {
            var installation = host.Server.LicenseLogin.DescribeInstallation();
            Assert.IsNotNull(installation.DataId);
            StringAssert.Contains(File.ReadAllText(fallbackFile(root)), installation.DataId);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task APlaceThatCannotBeWritten_LeavesTheIdOut_AndSaysWhy() {
        var root = newRoot();
        // a file where the folder has to be
        Directory.CreateDirectory(Path.Combine(root, "relatude.db"));
        File.WriteAllText(Path.Combine(root, "relatude.db", "installation"), "");
        var host = TestServerHost.Start(root);
        try {
            var installation = host.Server.LicenseLogin.DescribeInstallation();
            Assert.IsNull(installation.DataId);
            Assert.IsNotNull(installation.DataIdProblem);
            Assert.AreEqual(2, partsOf(installation.Key).Length, "server id:host id, without the data id");
            var status = await host.Server.LicenseLogin.DescribeAsync();
            Assert.AreEqual(installation.Key, status.Installation.Key, "the Services page shows the key that is sent");
        } finally {
            await host.DisposeAsync();
        }
    }

    // ---- the host part ----

    static Func<string, string?> environment(string? site, string? slot = "Production", string? owner = "11111111-2222-3333-4444-555555555555+my-rg-WestEuropewebspace") {
        var values = new Dictionary<string, string?> {
            ["WEBSITE_SITE_NAME"] = site,
            ["WEBSITE_SLOT_NAME"] = slot,
            ["WEBSITE_OWNER_NAME"] = owner,
            ["WEBSITE_INSTANCE_ID"] = Guid.NewGuid().ToString("N"), // the worker, which must not count
        };
        return name => values.GetValueOrDefault(name);
    }

    [TestMethod]
    public void OnAzureAppService_TheHostIsTheAppAndSlot_NotTheWorker() {
        var site = InstallationIdentity.Calculate(environment("site-a"));
        Assert.AreEqual(32, site.Length);
        Assert.AreEqual(site, InstallationIdentity.Calculate(environment("site-a")), "another worker, the same app");
        Assert.AreNotEqual(site, InstallationIdentity.Calculate(environment("site-b")), "another app on the same plan");
        Assert.AreNotEqual(site, InstallationIdentity.Calculate(environment("site-a", slot: "staging")), "another slot of the same app");
        Assert.AreNotEqual(site, InstallationIdentity.Calculate(environment("site-a", owner: "99999999-2222-3333-4444-555555555555+other-rg-WestEuropewebspace")), "the same name in another resource group");
        Assert.AreEqual("Azure App Service app \"site-a\", slot Production", InstallationIdentity.Describe(environment("site-a")));
        Assert.AreEqual("this machine", InstallationIdentity.Describe(environment(null)));
    }

    static RelatudeDBServerSettings diskSettings(params string[] folders) {
        var template = RelatudeDBServerSettings.CreateDefault().ContainerSettings![0];
        var containers = folders.Select((folder, i) => {
            var container = TestServerHost.MemoryContainer(template, "Database " + i);
            container.AutoOpen = false;
            container.WaitUntilOpen = false;
            container.IOSettings![0].IOType = IOTypes.LocalDisk;
            container.IOSettings[0].Path = folder;
            return container;
        }).ToArray();
        return new RelatudeDBServerSettings { Id = Guid.NewGuid(), Name = "Installation key test", ContainerSettings = containers, DefaultStoreId = containers[^1].Id };
    }
}
