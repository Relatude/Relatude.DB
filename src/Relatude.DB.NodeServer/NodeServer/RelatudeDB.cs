using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;

namespace Relatude.DB.NodeServer;

/// <summary>
/// The default database, for code with no request: services, jobs, start-up code. AddRelatudeDB() adds it as a
/// singleton. Each store it hands out is named for who it reads as. Never dispose one: that closes the database.
/// </summary>
public interface IRelatudeDB {
    /// <summary>False while the database is still starting. The stores throw until it is open.</summary>
    bool IsOpen { get; }
    /// <summary>No access checks: published content, in no culture. For trusted code only.</summary>
    NodeStore AdminDB { get; }
    /// <summary>As a user: their read access, published content, in the culture given. The user must be one the database has.</summary>
    NodeStore AsUser(Guid userId, string? cultureCode = null);
    /// <summary>As a visitor who is not signed in.</summary>
    NodeStore AsAnonymous(string? cultureCode = null);
}

/// <summary>
/// <see cref="IRelatudeDB"/> over the application's server (<see cref="RelatudeDBRuntime"/>), a given server, or a
/// given store, as tests and tools with no server have.
/// </summary>
public sealed class RelatudeDB : IRelatudeDB {
    readonly Func<RelatudeDBServer?>? _server;
    readonly NodeStore? _store;
    sealed record Derived(NodeStore Root, NodeStore Admin);
    Derived? _derived;

    public RelatudeDB() => _server = () => RelatudeDBRuntime.IsInitialized ? RelatudeDBRuntime.Server : null;
    public RelatudeDB(RelatudeDBServer server) => _server = () => server;
    public RelatudeDB(NodeStore store) => _store = store;

    public bool IsOpen => _store != null
        ? _store.Datastore.State == DataStoreState.Open
        : _server!() is { } server && server.DefaultStoreIsOpen();

    public NodeStore AdminDB {
        get {
            var root = this.root();
            // a server's own store reads as the master admin already, and stays the object its container holds
            if (root.QueryContext.Equals(QueryContext.MasterAdmin)) return root;
            // by the root it was made of: a restarted server has a new store
            var derived = _derived;
            if (derived == null || !ReferenceEquals(derived.Root, root)) _derived = derived = new(root, root.Context.With(QueryContext.MasterAdmin).Create());
            return derived.Admin;
        }
    }

    public NodeStore AsUser(Guid userId, string? cultureCode = null) =>
        root().Context.With(QueryContext.Anonymous.User(userId).Culture(cultureCode)).Create();

    public NodeStore AsAnonymous(string? cultureCode = null) => AsUser(Guid.Empty, cultureCode);

    NodeStore root() {
        if (_store != null) return _store;
        var server = _server!() ?? throw new InvalidOperationException("Relatude.DB is not started yet. ");
        var container = server.DefaultContainer;
        if (container?.Store is { } store) return store;
        if (container?.StartUpException is { } failure) throw new InvalidOperationException("Startup exception: " + failure.Message + " ", failure);
        throw new InvalidOperationException("Default database store is not ready. ");
    }
}
