using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.Server;

/// <summary>
/// There are two restarts, and they are not variations of each other. A soft restart rebuilds the
/// databases inside the running process, so it is the only one that behaves the same everywhere and
/// the only one that can be observed from end to end here. Stopping the host only asks the host to
/// stop - what happens next belongs to whatever is supervising the process, so the tests stop where
/// the server's responsibility does.
/// </summary>
[TestClass]
public class ServerRestartTests {

    string _root = string.Empty;

    [TestInitialize]
    public void CreateRoot() {
        _root = Path.Combine(Path.GetTempPath(), "relatude.restart." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void DeleteRoot() {
        try { Directory.Delete(_root, true); } catch { }
    }

    [TestMethod]
    public async Task SoftRestart_ClosesTheOldDatabasesAndOpensNewOnes() {
        var host = TestServerHost.Start(_root, databases: 2);
        try {
            var before = host.Server.GetContainers();
            var instanceId = host.Server.InstanceId;
            Assert.IsTrue(before.All(c => c.IsOpen()));

            Assert.IsTrue(await host.Server.SoftRestartAsync());

            Assert.AreEqual(2, host.Closed.Count, "every open database should have been closed");
            var after = host.Server.GetContainers();
            Assert.AreEqual(2, after.Length);
            Assert.IsTrue(after.All(c => c.IsOpen()), "AutoOpen databases should be open again");
            Assert.IsFalse(after.Intersect(before).Any(), "the containers should have been rebuilt, not reused");
            Assert.AreEqual(1, host.Server.RestartCount);
            Assert.AreEqual(instanceId, host.Server.InstanceId, "a soft restart must not look like a new process");
            Assert.IsFalse(host.Server.IsRestarting);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SoftRestart_ReadsTheSettingsAgain() {
        var host = TestServerHost.Start(_root, databases: 1);
        try {
            var readsAtStartup = host.Settings.ReadCount;
            var template = host.Settings.Settings.ContainerSettings![0];
            // the whole point of a soft restart: a database added to the settings file shows up
            host.Settings.Settings = new RelatudeDBServerSettings {
                Id = host.Settings.Settings.Id,
                Name = host.Settings.Settings.Name,
                ContainerSettings = [template, TestServerHost.MemoryContainer(template, "Added")],
                DefaultStoreId = host.Settings.Settings.DefaultStoreId,
            };

            Assert.IsTrue(await host.Server.SoftRestartAsync());

            Assert.AreEqual(readsAtStartup + 1, host.Settings.ReadCount);
            var containers = host.Server.GetContainers();
            Assert.AreEqual(2, containers.Length);
            Assert.IsNotNull(containers.SingleOrDefault(c => c.Settings.Name == "Added"));
            Assert.IsTrue(containers.All(c => c.IsOpen()));
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SoftRestart_KeepsTheDefaultDatabaseResolvable() {
        var host = TestServerHost.Start(_root, databases: 2);
        try {
            Assert.IsTrue(host.Server.DefaultStoreIsOpen());
            var defaultId = host.Server.DefaultContainer!.Settings.Id;

            Assert.IsTrue(await host.Server.SoftRestartAsync());

            // the container object is a new one, but it still has to be the default and still be open,
            // or every RelatudeDBRuntime.Database in the application breaks after a restart
            Assert.IsNotNull(host.Server.DefaultContainer);
            Assert.AreEqual(defaultId, host.Server.DefaultContainer!.Settings.Id);
            Assert.IsTrue(host.Server.DefaultStoreIsOpen());
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SoftRestart_IsRefusedWhileTheHostIsStopping() {
        var host = TestServerHost.Start(_root);
        try {
            host.Server.BeginShutdown();
            // rebuilding databases that the shutdown is about to dispose helps nobody
            Assert.IsFalse(await host.Server.SoftRestartAsync());
            Assert.AreEqual(0, host.Server.RestartCount);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SoftRestart_CanRunMoreThanOnce() {
        var host = TestServerHost.Start(_root);
        try {
            Assert.IsTrue(await host.Server.SoftRestartAsync());
            Assert.IsTrue(await host.Server.SoftRestartAsync());
            Assert.AreEqual(2, host.Server.RestartCount);
            Assert.IsTrue(host.Server.GetContainers().Single().IsOpen());
            Assert.IsNotNull(host.Server.LastRestartUtc);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SoftRestart_IsDisabledByOptions() {
        var host = TestServerHost.Start(_root);
        try {
            host.Server.Options!.AllowedRestarts = RestartOptions.StopHost;
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => host.Server.SoftRestartAsync());
            Assert.IsFalse(host.Server.GetRestartCapabilities().CanSoftRestart);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task StopHost_SignalsTheHostToStop() {
        var host = TestServerHost.Start(_root, databases: 2);
        try {
            await host.App.StartAsync();
            Assert.IsFalse(host.Server.IsShuttingDown);

            Assert.IsTrue(host.Server.StopHost());

            // StopHost returns before the stop is signalled, so that the request that asked for it can
            // still answer. ApplicationStopping - and with it BeginShutdown - follows a moment later.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!host.Server.IsShuttingDown && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            Assert.IsTrue(host.Server.IsShuttingDown, "the host should have begun stopping");
            // the databases stay open until the requests have drained, which is ApplicationStopped's job
            Assert.IsTrue(host.Server.GetContainers().All(c => c.IsOpen()));

            await host.App.StopAsync();
            Assert.IsTrue(host.Server.IsShutDown);
            Assert.AreEqual(2, host.Closed.Count);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task StopHost_IsDisabledByOptions() {
        var host = TestServerHost.Start(_root);
        try {
            host.Server.Options!.AllowedRestarts = RestartOptions.Soft;
            Assert.ThrowsException<InvalidOperationException>(() => host.Server.StopHost());
            Assert.IsFalse(host.Server.GetRestartCapabilities().CanStopHost);
            Assert.IsFalse(host.Server.IsShuttingDown);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RestartCapabilities_DescribeTheHost() {
        var host = TestServerHost.Start(_root);
        try {
            var caps = host.Server.GetRestartCapabilities();
            Assert.IsTrue(caps.CanSoftRestart);
            Assert.IsTrue(caps.CanStopHost, "a server started with a WebApplication has a host to stop");
            Assert.IsFalse(string.IsNullOrWhiteSpace(caps.HostDescription));
            Assert.AreEqual(host.Server.InstanceId, caps.InstanceId);
            Assert.AreEqual(0, caps.RestartCount);
            Assert.IsFalse(caps.IsRestarting);
            Assert.IsFalse(caps.IsShuttingDown);
            // whatever the host turns out to be, an unidentified one must warn rather than stay silent
            if (caps.HostRestartsAutomatically != true) Assert.IsNotNull(caps.StopHostWarning);
        } finally {
            await host.DisposeAsync();
        }
    }
}
