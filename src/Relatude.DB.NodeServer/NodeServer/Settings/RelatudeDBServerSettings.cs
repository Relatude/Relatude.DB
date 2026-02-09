using Relatude.DB.AI;

namespace Relatude.DB.NodeServer.Settings;
public class RelatudeDBServerSettings {

    // STATIC SETTINGS (set at startup):
    // Required settings, stored securely in appsettings or environment variables
    public string? MasterUserName { get; set; }
    public string? MasterPassword { get; set; }
    //public string TokenEncryptionSalt { get; set; } = SecureGuid.New().ToString();
    public string TokenEncryptionSecret { get; set; } = SecureGuid.New().ToString();
    public int TokenCookieMaxAgeInSec { get; set; } = 60 * 60 * 24 * 10; // 10 days

    public string? DBAdminUIUrlPath { get; set; }
    public string? DBSettingsFilePath { get; set; }

    // Optional  settings, defaults ok for most scenarios
    public Guid Id { get; set; } = SecureGuid.New(); // Unique server ID, used for multiple server scenarios
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

    // Database settings
    public AIProviderSettings[]? AISettings { get; set; }

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
            IoFiles = [io.Id],
            IoBackup = io.Id,
            IoLog = io.Id,
            DatamodelSources = [new DatamodelSource()
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

