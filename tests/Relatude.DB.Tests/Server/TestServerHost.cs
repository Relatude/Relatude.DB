using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Relatude.DB.DataStores;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;
using Relatude.DB.Nodes;

namespace Relatude.Server;

/// <summary>
/// A real server on memory IO providers, opened synchronously, on a host that is built but not
/// started - the same shape the CLI uses, so a test can drive the shutdown and the restart directly.
/// Call <see cref="App"/>.StartAsync() first when the test needs the host lifetime to be running.
/// </summary>
sealed class TestServerHost(WebApplication app, RelatudeDBServer server, List<NodeStore> closed, MutableSettings settings) {
    public WebApplication App => app;
    public RelatudeDBServer Server => server;
    /// <summary>Every store handed to OnStoreClose, in the order it was closed.</summary>
    public List<NodeStore> Closed => closed;
    /// <summary>The settings the server reads on every (re)load, so a test can change them and restart.</summary>
    public MutableSettings Settings => settings;
    public async Task DisposeAsync() {
        try { server.Shutdown(); } catch { }
        await app.DisposeAsync();
    }

    public static TestServerHost Start(string root, int databases = 1) {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions {
            ContentRootPath = root,
            ApplicationName = "relatude.tests",
            EnvironmentName = "Test",
        });
        builder.Services.AddSingleton<IServer, NoServer>();
        var app = builder.Build();
        var closed = new List<NodeStore>();
        var settings = new MutableSettings(MemorySettings(databases));
        var options = new ServerOptions {
            SettingsLoader = settings,
            ConfigurationSectionName = null, // no appsettings overlay in the tests
            DefaultDataFolderPath = root,
            DefaultTempFolderPath = Path.Combine(root, "temp"),
            OnStoreClose = store => { lock (closed) closed.Add(store); },
        };
        var server = new RelatudeDBServer(string.Empty);
        server.StartAsync(app, options).Wait();
        return new TestServerHost(app, server, closed, settings);
    }

    public static RelatudeDBServerSettings MemorySettings(int databases) {
        var template = RelatudeDBServerSettings.CreateDefault().ContainerSettings![0];
        var containers = new List<NodeStoreContainerSettings>();
        for (var i = 0; i < databases; i++) containers.Add(MemoryContainer(template, "Database " + i));
        return new RelatudeDBServerSettings {
            Id = Guid.NewGuid(),
            Name = "Relatude.DB Test Server",
            ContainerSettings = containers.ToArray(),
            DefaultStoreId = containers[0].Id,
        };
    }

    public static NodeStoreContainerSettings MemoryContainer(NodeStoreContainerSettings template, string name) {
        var io = new IOSettings { Id = Guid.NewGuid(), Name = "Memory " + name, IOType = IOTypes.Memory };
        return new NodeStoreContainerSettings {
            Id = Guid.NewGuid(),
            Name = name,
            AutoOpen = true,
            WaitUntilOpen = true, // open before StartAsync returns, so the tests need no polling
            IOSettings = [io],
            IoDatabase = io.Id,
            IoBackup = io.Id,
            IoLog = io.Id,
            FileStoreSettings = [],
            DatamodelSources = template.DatamodelSources,
            LocalSettings = new SettingsLocal {
                AutoBackUp = false,
                AutoTruncate = false,
                AutoSaveIndexStates = false,
                AutoDequeTasks = false,
            },
        };
    }
}

/// <summary>Settings a test can change between reads, so a restart can pick something new up.</summary>
sealed class MutableSettings(RelatudeDBServerSettings settings) : ISettingsLoader {
    public RelatudeDBServerSettings Settings { get; set; } = settings;
    public int ReadCount { get; private set; }
    public Task<RelatudeDBServerSettings> ReadAsync() {
        ReadCount++;
        return Task.FromResult(Settings);
    }
    public Task WriteAsync(RelatudeDBServerSettings s) => Task.CompletedTask; // never touch disk
}

sealed class NoServer : IServer {
    public IFeatureCollection Features { get; } = new FeatureCollection();
    public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
        => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Dispose() { }
}
