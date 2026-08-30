using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;

namespace Relatude.DB.NodeServer.Settings;
public class RelatudeDBServerSettings {

    public Guid Id { get; set; } = SecureGuid.New(); // Unique server ID, used for multiple server scenarios
    
    // STATIC SETTINGS (set at startup):
    // Required settings, stored securely in appsettings or environment variables

    public string? MasterUserName { get; set; }
    public string? MasterPassword { get; set; }
    public string? TokenEncryptionSecret { get; set; } // No default, should be unique and secret for each installation
    public bool AllowMasterLoginOutsideLocalhost { get; set; } = false;
    /// <summary>Skips the login entirely for a request that really came from a browser on this
    /// machine. What counts as "this machine" is decided narrowly, and deliberately so: see
    /// <see cref="LocalRequest.IsLocalhost"/>. A loopback peer alone is not enough, because any
    /// reverse proxy in front of the server makes every request arrive from loopback.</summary>
    public bool NoLoginRequiredForLocalhost { get; set; } = true;
    public int TokenCookieMaxAgeInSec { get; set; } = 60 * 60 * 24 * 10; // 10 days

    public string? DBAdminUIUrlPath { get; set; }
    public string? DBSettingsFilePath { get; set; }

    // Optional  settings, defaults ok for most scenarios
    public bool TokenLockedToIP { get; set; } = false;
    public bool TokenCookieSecure { get; set; } = true;
    public bool TokenCookieSameSite { get; set; } = true;


    // DYNAMIC SETTINGS (can be changed at runtime):

    // Server settings
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string TokenCookieName { get; set; } = "RelatudeDBToken";
    public Guid DefaultStoreId { get; set; }

    // Each database container settings
    public NodeStoreContainerSettings[]? ContainerSettings { get; set; }

    public static RelatudeDBServerSettings CreateDefault() {
        var io = new IOSettings() {
            Id = Guid.NewGuid(),
            Name = "Local disk",
            Path = Defaults.DataFolderPath,
            IOType = IOTypes.LocalDisk,
        };
        var local = new SettingsLocal() {
        };
        var c = new NodeStoreContainerSettings() {
            Id = Guid.NewGuid(),
            Name = "MyDatabase",
            AutoOpen = true,
            LocalSettings = local,
            IOSettings = [io],
            IoDatabase = io.Id,
            FileStoreSettings = [],
            IoBackup = io.Id,
            IoLog = io.Id,
            DatamodelSources = [new ()
            {
                Id = Guid.NewGuid(),
                Name = "Demo",
                Type = DatamodelSourceType.AssemblyNameReference,
                Namespace = "Relatude.DB.Demo.Models",
                Reference = "Relatude.DB.NodeStore",
            }
            ],
        };
        return new RelatudeDBServerSettings() {
            Name = "Relatude.DB Server",
            ContainerSettings = [c],
            DefaultStoreId = c.Id,
        };
    }
}

