using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.Cli;

/// <summary>
/// Opens a database the same way a hosted application does: the server library reads the very same
/// relatude.db.json, creates the same IO providers, index engines and datamodel sources, and opens one
/// container. Nothing is served over HTTP - the web host is built but never started, it only supplies the
/// content root the settings are resolved against.
///
/// <para>Unless --allow-background is given, everything the store would otherwise do on its own
/// (auto backup, auto truncate, index state snapshots, the task queue) is turned off, so a command reads
/// the database without starting work in the background that it cannot finish.</para>
/// </summary>
public sealed class StoreHost : IDisposable {
    public static readonly string[] Options = ["allow-background", "no-ai"];

    StoreHost() { }
    public required RelatudeDBServer Server { get; init; }
    public required NodeStoreContainer Container { get; init; }
    public required RelatudeDBServerSettings ServerSettings { get; init; }
    public NodeStore Store => Container.Store ?? throw new CliException("The database is not open.");
    public NodeStoreContainerSettings Settings => Container.Settings;
    public Datamodel Datamodel => Store.Datastore.Datamodel;

    public static async Task<StoreHost> OpenAsync(CommandArgs args, Target target) {
        if (!target.SettingsExists) {
            throw new CliException("No " + Defaults.SettingsFileName + " found." + Environment.NewLine + target.Describe()
                + Environment.NewLine + "Point the tool at the application folder with --project, name the file with --settings,"
                + Environment.NewLine + "or create one with: relatude init");
        }
        if (new FileInfo(target.SettingsPath).Length == 0) {
            throw new CliException("The settings file is empty: " + target.SettingsPath
                + Environment.NewLine + "Write a new one with: relatude init --force");
        }
        target.RegisterAssemblyProbing();
        Con.Info("Opening " + target.SettingsPath);

        RelatudeDBServerSettings? serverSettings = null;
        Guid selected = Guid.Empty;
        var options = new ServerOptions {
            SettingsLoader = new LocalSettingsLoaderFile(target.SettingsPath),
            DefaultTempFolderPath = Path.Combine(Path.GetTempPath(), "relatude.db.cli"),
            OnServerSettingsInit = s => {
                serverSettings = s;
                selected = SettingsReader.SelectContainerId(s, target.Store);
            },
            OnContainerSettingsInit = c => {
                // only the selected container is opened, and the command waits for it
                c.AutoOpen = c.Id == selected;
                c.WaitUntilOpen = true;
                if (args.Flag("no-ai")) c.AiProvider = null;
            },
            OnStoreSettingsInit = (local, _) => {
                local.WriteSystemLogConsole = !Con.Quiet && Con.Verbose;
                if (args.Flag("allow-background")) return;
                local.AutoBackUp = false;
                local.AutoTruncate = false;
                local.AutoSaveIndexStates = false;
                local.AutoDequeTasks = false;
            },
            OnDatamodelInit = (dm, _) => {
                if (ModelSource.IsExplicit(args, target)) ModelSource.AddTo(dm, args, target);
            },
        };
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions {
            ContentRootPath = target.Root,
            ApplicationName = "relatude",
            EnvironmentName = "Production",
        });
        builder.Services.AddSingleton<IServer, NoServer>(); // the host is built for its content root, never started
        var app = builder.Build();
        var server = new RelatudeDBServer(string.Empty);
        if (!RelatudeDBRuntime.IsInitialized) RelatudeDBRuntime.Initialize(server);
        try {
            await server.StartAsync(app, options);
        } catch (Exception err) {
            server.Shutdown();
            throw describe(err, target);
        }
        if (serverSettings == null) throw new CliException("The settings file could not be read: " + target.SettingsPath);
        if (!server.Containers.TryGetValue(selected, out var container)) {
            server.Shutdown();
            throw new CliException("No database container to open in " + target.SettingsPath);
        }
        if (container.StartUpException != null) {
            server.Shutdown();
            throw describe(container.StartUpException, target);
        }
        if (!container.IsOpen()) {
            server.Shutdown();
            throw new CliException("The database did not open. State: " + (container.Store?.State.ToString() ?? "not initialized"));
        }
        Con.Info($"Database \"{container.Settings.Name}\" is open.");
        return new StoreHost { Server = server, Container = container, ServerSettings = serverSettings };
    }

    /// <summary>Turns the two failures that actually happen in practice into something actionable.</summary>
    static Exception describe(Exception err, Target target) {
        for (var e = err; e != null; e = e.InnerException) {
            if (e is IOException io && (io.Message.Contains("another process") || io.Message.Contains("being used"))) {
                return new CliException("The database files are locked by another process - the application using "
                    + target.Root + " is probably running. Stop it and try again." + Environment.NewLine + io.Message, err);
            }
            if (e is FileNotFoundException fnf && fnf.Message.Contains("Could not load file or assembly")) {
                return new CliException("A model assembly could not be loaded: " + fnf.Message + Environment.NewLine
                    + "Build the application first, or point at its output folder with --bin." + Environment.NewLine + target.Describe(), err);
            }
        }
        return new CliException(err.Message, err);
    }

    /// <summary>
    /// The store's own report on itself. It is only filled in when the reader gets the write lock, which a
    /// background flush can hold, so it is asked for again until it comes back fresh.
    /// </summary>
    public DataStoreInfo Info() {
        var info = Store.Datastore.GetInfo();
        for (var attempt = 0; attempt < 40 && !info.IsFresh; attempt++) {
            Thread.Sleep(50);
            info = Store.Datastore.GetInfo();
        }
        if (!info.IsFresh) Con.Warn("The database stayed busy: some of the numbers below are stale or missing.");
        return info;
    }

    public void Dispose() {
        try {
            Server.Shutdown();
        } catch (Exception err) {
            Con.Warn("Closing the database failed: " + err.Message);
        }
    }

    /// <summary>
    /// Stands in for Kestrel. The web host exists only to resolve the content root and the application
    /// lifetime the server library asks for; nothing here can accept a request.
    /// </summary>
    sealed class NoServer : IServer {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
            => throw new NotSupportedException("The command line tool does not serve requests.");
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }
}
