using Relatude.DB.Nodes;

namespace Relatude.DB.NodeServer;

/// <summary>
/// A library's part in the start of every database, registered as a service, the way a library registers its
/// custom settings. It is called with each store as it is set up, before it opens and after
/// <see cref="ServerOptions.OnStoreInit"/>: the place to register transaction plugins and task runners without
/// touching the application's <see cref="ServerOptions"/>, so neither the order of the setup calls nor a later
/// assignment of OnStoreInit can lose it.
/// </summary>
public interface IStoreInitializer {
    void Initialize(NodeStore store);
}
