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
        var host = start();
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
        var host = start(databases: 3);
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
        var host = start();
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
        var host = start();
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
        var host = start(databases: 2);
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

    sealed class Host(WebApplication app, RelatudeDBServer server, List<NodeStore> closed) {
        public WebApplication App => app;
        public RelatudeDBServer Server => server;
        public List<NodeStore> Closed => closed;
        public async Task DisposeAsync() {
            try { server.Shutdown(); } catch { }
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// A real server on memory IO providers, opened synchronously, on a host that is built but not
    /// started - the same shape the CLI uses, so a test can drive the shutdown directly.
    /// </summary>
    Host start(int databases = 1) {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions {
            ContentRootPath = _root,
            ApplicationName = "relatude.tests",
            EnvironmentName = "Test",
        });
        builder.Services.AddSingleton<IServer, NoServer>();
        var app = builder.Build();
        var closed = new List<NodeStore>();
        var options = new ServerOptions {
            SettingsLoader = new FixedSettings(memorySettings(databases)),
            ConfigurationSectionName = null, // no appsettings overlay in the tests
            DefaultDataFolderPath = _root,
            DefaultTempFolderPath = Path.Combine(_root, "temp"),
            OnStoreClose = store => { lock (closed) closed.Add(store); },
        };
        var server = new RelatudeDBServer(string.Empty);
        server.StartAsync(app, options).Wait();
        return new Host(app, server, closed);
    }

    static RelatudeDBServerSettings memorySettings(int databases) {
        var template = RelatudeDBServerSettings.CreateDefault().ContainerSettings![0];
        var containers = new List<NodeStoreContainerSettings>();
        for (var i = 0; i < databases; i++) {
            var io = new IOSettings { Id = Guid.NewGuid(), Name = "Memory " + i, IOType = IOTypes.Memory };
            containers.Add(new NodeStoreContainerSettings {
                Id = Guid.NewGuid(),
                Name = "Database " + i,
                AutoOpen = true,
                WaitUntilOpen = true, // open before StartAsync returns, so the tests need no polling
                IOSettings = [io],
                IoDatabase = io.Id,
                IoBackup = io.Id,
                IoLog = io.Id,
                FileStoreSettings = [],
                DatamodelSources = template.DatamodelSources,
                LocalSettings = new SettingsLocal {
                    PersistedValueIndexEngine = PersistedValueIndexEngine.Memory,
                    PersistedTextIndexEngine = PersistedTextIndexEngine.Memory,
                    AutoBackUp = false,
                    AutoTruncate = false,
                    AutoSaveIndexStates = false,
                    AutoDequeTasks = false,
                },
            });
        }
        return new RelatudeDBServerSettings {
            Id = Guid.NewGuid(),
            Name = "Relatude.DB Test Server",
            ContainerSettings = containers.ToArray(),
            DefaultStoreId = containers[0].Id,
        };
    }

    sealed class FixedSettings(RelatudeDBServerSettings settings) : ISettingsLoader {
        public Task<RelatudeDBServerSettings> ReadAsync() => Task.FromResult(settings);
        public Task WriteAsync(RelatudeDBServerSettings s) => Task.CompletedTask; // never touch disk
    }

    sealed class NoServer : IServer {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
            => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }
}
