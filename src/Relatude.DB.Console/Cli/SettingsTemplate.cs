using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.NodeServer;
using Relatude.DB.NodeServer.Settings;

namespace Relatude.DB.Cli;

/// <summary>
/// The relatude.db.json that "init" and "new" write: one database on local disk with the native
/// engines and a file store, the engine's own model, and optionally the caller's model namespace. The
/// default the server writes when it finds no file points at the bundled demo model, which is almost
/// never what is wanted.
/// </summary>
public static class SettingsTemplate {
    /// <param name="databaseName">Name of the database container.</param>
    /// <param name="dataPath">Data folder, relative to the settings file.</param>
    /// <param name="modelNamespace">Namespace holding the caller's model types, or null to leave it out.</param>
    /// <param name="assemblyName">Assembly the model types live in, null for the entry assembly.</param>
    /// <param name="user">Admin UI user, or null to leave the login unset.</param>
    /// <param name="password">Admin UI password, or null.</param>
    /// <param name="waitUntilOpen">Whether the application waits for the database to open before it serves requests.</param>
    public static RelatudeDBServerSettings Create(string databaseName, string dataPath, string? modelNamespace, string? assemblyName, string? user, string? password, bool waitUntilOpen) {
        var io = new IOSettings {
            Id = Guid.NewGuid(),
            Name = "Local disk",
            Path = dataPath,
            IOType = IOTypes.LocalDisk,
        };
        var container = new NodeStoreContainerSettings {
            Id = Guid.NewGuid(),
            Name = databaseName,
            AutoOpen = true,
            WaitUntilOpen = waitUntilOpen,
            LocalSettings = SettingsLocal.CreateWithNativeEngines(),
            IOSettings = [io],
            IoDatabase = io.Id,
            IoBackup = io.Id,
            IoLog = io.Id,
            FileStoreSettings = [new FileStoreSettings {
                Id = Guid.NewGuid(),
                IoProviderId = io.Id,
                StoreType = FileStoreEngine.MultiFile,
                MultiFileFolderDepth = 2,
            }],
            DatamodelSources = sources(modelNamespace, assemblyName),
        };
        return new RelatudeDBServerSettings {
            Name = "Relatude.DB Server",
            Id = Guid.NewGuid(),
            ContainerSettings = [container],
            DefaultStoreId = container.Id,
            MasterUserName = user,
            MasterPassword = password,
            TokenEncryptionSecret = Guid.NewGuid().ToString(),
        };
    }

    static DatamodelSource[] sources(string? modelNamespace, string? assemblyName) {
        // the engine's own model is always needed: it backs users, groups, collections and cultures
        var list = new List<DatamodelSource> {
            new() {
                Id = Guid.NewGuid(),
                Name = "Native",
                Type = DatamodelSourceType.TypeReference,
                Namespace = ModelSource.NativeNamespace,
                Reference = "Relatude.DB.NodeStore",
            },
        };
        if (modelNamespace != null) {
            list.Insert(0, new DatamodelSource {
                Id = Guid.NewGuid(),
                Name = "Model",
                Type = DatamodelSourceType.TypeReference,
                Namespace = modelNamespace,
                Reference = assemblyName,
            });
        }
        return [.. list];
    }
}
