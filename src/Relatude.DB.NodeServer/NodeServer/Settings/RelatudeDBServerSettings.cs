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

    // Relatude.License: the license this installation runs under, and whether its users may sign in here with it.
    /// <summary>The license key from the Relatude.License portal: the license's id. With <see cref="ApiKey"/> it
    /// lets this installation report in and use the Relatude services.</summary>
    public string? LicenseKey { get; set; }
    /// <summary>An API key issued for that license in the portal. It lets whoever holds it act as this
    /// installation towards the license server, so keep it in configuration or user secrets rather than here.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Offers "Sign in with Relatude.License" on the login page. Who gets in is the license
    /// server's decision: the owner of the license, and the users the owner has granted this installation
    /// to. The master login stays as the fallback. See <see cref="LicenseLogin"/>.</summary>
    public bool AllowLicenseeAdminLogin { get; set; } = false;
    /// <summary>
    /// Stops this installation reporting in to the license server. Written as the exception rather
    /// than the rule so that the reporting an installation has always done is what an absent
    /// setting means: false, the default, keeps it. What one report carries, and what is lost by
    /// turning it off, is on the setting in the admin UI (<c>SettingsCatalog</c>) and in
    /// <see cref="LicenseLogin.StartHeartbeat"/>.
    /// <para>Read at each beat, so switching it on stops the next one without a restart.</para>
    /// </summary>
    public bool DisableHeartbeat { get; set; }
    /// <summary>Where the Relatude.License server is. Only a self-hosted or test server needs this changed.</summary>
    public string LicenseServerUrl { get; set; } = Defaults.LicenseServerUrl;

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
        var local = SettingsLocal.CreateWithNativeEngines();
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
                Type = DatamodelSourceType.CompiledTypes,
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

