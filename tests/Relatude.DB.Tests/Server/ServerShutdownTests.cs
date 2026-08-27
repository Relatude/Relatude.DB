using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;
using Relatude.DB.Nodes;

namespace Relatude.Server;

/// <summary>
/// Stopping the host closes the databases in two steps: ApplicationStopping only quiesces (no new
/// database is opened, the open ones are flushed) because the web server has not drained its
/// requests yet, and ApplicationStopped disposes. Hosts that are never started - the CLI - call
/// Shutdown directly, so it has to do both, exactly once, whoever calls it.
/// </summary>
[TestClass]
public class ServerShutdownTests {

    string _root = string.Empty;

    [TestInitialize]
    public void CreateRoot() {
        _root = Path.Combine(Path.GetTempPath(), "relatude.shutdown." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void DeleteRoot() {
        try { Directory.Delete(_root, true); } catch { }
    }

    [TestMethod]
    public async Task BeginShutdown_LeavesTheDatabasesOpen() {
        var host = TestServerHost.Start(_root);
        try {
            var container = host.Server.GetContainers().Single();
            Assert.IsTrue(container.IsOpen());
            host.Server.BeginShutdown();
            Assert.IsTrue(host.Server.IsShuttingDown);
            Assert.IsFalse(host.Server.IsShutDown);
            // the requests still draining in the web server are using this store
            Assert.IsTrue(container.IsOpen(), "the first phase must not dispose anything");
            Assert.AreEqual(0, host.Closed.Count);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Shutdown_DisposesEveryDatabase() {
        var host = TestServerHost.Start(_root, databases: 3);
        try {
            var containers = host.Server.GetContainers();
            Assert.AreEqual(3, containers.Length);
            Assert.IsTrue(containers.All(c => c.IsOpen()));
            host.Server.Shutdown();
            Assert.IsTrue(host.Server.IsShuttingDown);
            Assert.IsTrue(host.Server.IsShutDown);
            foreach (var container in containers) {
                Assert.IsNull(container.Store, "\"" + container.Settings.Name + "\" was not disposed");
                Assert.AreEqual(DataStoreState.Disposed, container.GetStatusAndActivity().State);
            }
            // the close event used to be handed the store field after it had been nulled
            Assert.AreEqual(3, host.Closed.Count);
            Assert.IsTrue(host.Closed.All(s => s != null), "OnStoreClose was called without a store");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Shutdown_RunsOnlyOnce() {
        var host = TestServerHost.Start(_root);
        try {
            host.Server.Shutdown();
            host.Server.Shutdown();
            host.Server.BeginShutdown();
            Assert.AreEqual(1, host.Closed.Count, "the databases were closed more than once");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Open_AfterShutdown_IsRefused() {
        var host = TestServerHost.Start(_root);
        try {
            var container = host.Server.GetContainers().Single();
            host.Server.Shutdown();
            // an open landing after the shutdown would leave a database open, and unflushed, for the
            // rest of the process
            Assert.ThrowsException<InvalidOperationException>(() => container.Open());
            Assert.IsNull(container.Store);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task StoppingTheHost_ClosesTheDatabases() {
        var host = TestServerHost.Start(_root, databases: 2);
        try {
            await host.App.StartAsync();
            Assert.IsFalse(host.Server.IsShuttingDown);
            await host.App.StopAsync(); // ApplicationStopping, then the drain, then ApplicationStopped
            Assert.IsTrue(host.Server.IsShutDown);
            Assert.IsTrue(host.Server.GetContainers().All(c => c.Store == null));
            Assert.AreEqual(2, host.Closed.Count);
        } finally {
            await host.DisposeAsync();
        }
    }
}
