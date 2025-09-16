namespace Relatude.DB.NodeServer;
public class RelatudeDBServerSettings {
    public Guid Id { get; set; } = SecureGuid.New();
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? MasterUserName { get; set; }
    public string? MasterPassword { get; set; }
    public string TokenCookieName { get; set; } = "RelatudeDBToken";
    public string TokenEncryptionSalt { get; set; } = SecureGuid.New().ToString();
    public string TokenEncryptionSecret { get; set; } = SecureGuid.New().ToString();
    public int TokenCookieMaxAgeInSec { get; set; } = 60 * 60 * 24 * 10; // 10 days
    public bool TokenLockedToIP { get; set; } = false;
    public bool TokenCookieSecure { get; set; } = true;
    public bool TokenCookieSameSite { get; set; } = true;
    public Guid UserTokenId { get; set; }
    public Guid DefaultStoreId { get; set; }
    public NodeStoreContainerSettings[]? ContainerSettings { get; set; }
    public AISettings[]? AISettings { get; set; }
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

