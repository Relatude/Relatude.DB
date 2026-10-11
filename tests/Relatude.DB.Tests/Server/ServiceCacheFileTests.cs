using System.Diagnostics;
using Relatude.DB.Common;
using Relatude.DB.NodeServer;

namespace Relatude.Server;

/// <summary>
/// The services' answer caches are Native KV files, and a Native KV file is held by one cache at a time.
/// Every database without disk storage of its own keeps its local files in the server's data folder, so
/// there each database's files are named by its id: on one name the second and third database waited
/// 30 s per file at every open, and then went without (2026-10-11). A closed database lets go of its
/// files, or the one a soft restart opens in its place waits on them.
/// </summary>
[TestClass]
public class ServiceCacheFileTests {

    static readonly string[] serviceNames = ["imaging", "filetotext", "translation"];
    /// <summary>Well below one 30 s wait for a held file, well above three memory databases opening.</summary>
    static readonly TimeSpan noWaiting = TimeSpan.FromSeconds(15);

    string _root = string.Empty;
    // where a database without disk storage keeps its local files (RelatudeDBServer.DefaultSubDataFolderPath)
    string sharedFolder => Path.Combine(_root, "relatude.data");

    [TestInitialize]
    public void CreateRoot() {
        _root = Path.Combine(Path.GetTempPath(), "relatude.service-cache-files." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void DeleteRoot() {
        try { Directory.Delete(_root, true); } catch { }
    }

    static string ownedFile(string folder, string service, NodeStoreContainer container)
        => ServiceCaches.FilePath(folder, service, container.Settings.Id.ToString("N"))!;

    static bool isHeld(string path) {
        Assert.IsTrue(File.Exists(path), Path.GetFileName(path) + " is not there"); // or the open below throws for that
        try {
            using var _ = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        } catch (IOException) {
            return true;
        }
    }

    // a cache that could not be opened leaves the provider without one, with only a warning to show for it
    static void assertCached(NodeStoreContainer container) {
        var services = container.Store!.Services;
        var name = "\"" + container.Settings.Name + "\"";
        Assert.IsInstanceOfType<ICachingServiceProvider>(services.Imaging, name + " has no imaging cache");
        Assert.IsInstanceOfType<ICachingServiceProvider>(services.FileToText, name + " has no file-to-text cache");
        Assert.IsInstanceOfType<ICachingServiceProvider>(services.Translation, name + " has no translation cache");
    }

    [TestMethod]
    public async Task ThreeDatabasesInOneRoot_OpenWithoutWaitingOnTheCacheFiles() {
        var sw = Stopwatch.StartNew();
        var host = TestServerHost.Start(_root, databases: 3); // opened before Start returns (WaitUntilOpen)
        sw.Stop();
        try {
            var containers = host.Server.GetContainers();
            Assert.AreEqual(3, containers.Length);
            foreach (var container in containers) {
                Assert.IsTrue(container.IsOpen());
                assertCached(container);
                foreach (var service in serviceNames) {
                    var file = ownedFile(sharedFolder, service, container);
                    Assert.IsTrue(File.Exists(file), "\"" + container.Settings.Name + "\" has no file of its own for " + service);
                }
            }
            foreach (var service in serviceNames) {
                Assert.IsFalse(File.Exists(ServiceCaches.FilePath(sharedFolder, service)), "the plain " + service + " name is no one database's in a shared folder");
            }
            Assert.IsTrue(sw.Elapsed < noWaiting, "three databases took " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s to open");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SoftRestart_ReopensWithoutWaitingOnTheCacheFiles() {
        var host = TestServerHost.Start(_root, databases: 3);
        try {
            // the same settings come back, so the new containers open the very files the old ones held
            var sw = Stopwatch.StartNew();
            Assert.IsTrue(await host.Server.SoftRestartAsync());
            sw.Stop();
            var containers = host.Server.GetContainers();
            Assert.AreEqual(3, containers.Length);
            foreach (var container in containers) {
                Assert.IsTrue(container.IsOpen());
                assertCached(container);
            }
            Assert.IsTrue(sw.Elapsed < noWaiting, "the soft restart took " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AClosedDatabase_ReleasesItsCacheFiles() {
        var host = TestServerHost.Start(_root);
        try {
            var container = host.Server.GetContainers().Single();
            var files = serviceNames.Select(s => ownedFile(sharedFolder, s, container)).ToArray();
            foreach (var file in files) Assert.IsTrue(isHeld(file), "an open database holds " + Path.GetFileName(file));
            container.CloseIfOpen();
            foreach (var file in files) Assert.IsFalse(isHeld(file), Path.GetFileName(file) + " is still held after the close");
        } finally {
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// The default settings put the default database on disk in the server's data folder - the very
    /// folder a database without disk storage falls back to. The disk database keeps the plain names it
    /// always had, and with them what is already in them; the other gets files of its own.
    /// </summary>
    [TestMethod]
    public async Task ADatabaseOnDisk_KeepsThePlainNames_BesideOneWithout() {
        var sw = Stopwatch.StartNew();
        var host = TestServerHost.Start(_root, databases: 2, configure: s => {
            var onDisk = s.ContainerSettings![0];
            var io = new IOSettings { Id = Guid.NewGuid(), Name = "Local disk", IOType = IOTypes.LocalDisk, Path = "relatude.data" };
            onDisk.IOSettings = [io];
            onDisk.IoDatabase = io.Id;
            onDisk.IoBackup = io.Id;
            onDisk.IoLog = io.Id;
        });
        sw.Stop();
        try {
            var onDisk = host.Server.GetContainers().Single(c => c.Settings.Name == "Database 0");
            var inMemory = host.Server.GetContainers().Single(c => c.Settings.Name == "Database 1");
            assertCached(onDisk);
            assertCached(inMemory);
            foreach (var service in serviceNames) {
                Assert.IsTrue(File.Exists(ServiceCaches.FilePath(sharedFolder, service)), "the database on disk keeps the plain " + service + " name");
                Assert.IsTrue(File.Exists(ownedFile(sharedFolder, service, inMemory)), "the database without disk has a " + service + " file of its own");
                Assert.IsFalse(File.Exists(ownedFile(sharedFolder, service, onDisk)));
            }
            Assert.IsTrue(sw.Elapsed < noWaiting, "two databases took " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s to open");
        } finally {
            await host.DisposeAsync();
        }
    }
}
