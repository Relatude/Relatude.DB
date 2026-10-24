using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;
using Relatude.DB.NodeServer.Settings;

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

/// <summary>
/// A library's part in every database's datamodel, registered as a service as <see cref="IStoreInitializer"/> is. It
/// is called with each datamodel as it is built, after <see cref="ServerOptions.OnDatamodelInit"/> and before the
/// store is made from it: the place for a library to add its model types without the application listing them, so
/// neither the order of the setup calls nor a later assignment of OnDatamodelInit can lose them. The admin UI also
/// builds datamodels this way to check a draft of the model before it is saved, so an initializer only adds types.
/// </summary>
public interface IDatamodelInitializer {
    void Initialize(Datamodel datamodel, NodeStoreContainerSettingsBase database);
}

/// <summary>
/// A library's part when a database has opened, registered as a service as <see cref="IStoreInitializer"/> is. It
/// is called on a background thread after <see cref="ServerOptions.OnStoreOpen"/>, as
/// <see cref="ServerOptions.OnStoreOpenBackground"/> is, so the opening does not wait for it: the place to seed data,
/// or start work that needs the store open. The handlers are called one after the other, in the order they were
/// registered, and one that fails is logged without stopping the next.
/// </summary>
public interface IStoreOpenHandler {
    Task OpenedAsync(NodeStore store);
}
