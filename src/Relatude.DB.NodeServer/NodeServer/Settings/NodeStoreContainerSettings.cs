using Relatude.DB.DataStores;

namespace Relatude.DB.NodeServer.Settings;

public class NodeStoreContainerSettingsBase {
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool AutoOpen { get; set; }
    public bool WaitUntilOpen { get; set; }
}
public class FileStoreSettings {
    public Guid Id { get; set; }
    public Guid IoProviderId { get; set; }
    public int? MultiFileFolderDepth { get; set; }
    public FileStoreEngine StoreType { get; set; } = FileStoreEngine.SingleFile;
}
public enum AIIndexType {
    Memory,
    //MemoryTurboQuant,
    DiskISV,
    DiskHNSW,
}
public class AiIndexSettings {
    public AIIndexType IndexType { get; } = AIIndexType.Memory;
    public double MemoryLimitInMb { get; set; } = 100;
}
public class NodeStoreContainerSettings : NodeStoreContainerSettingsBase {
    public IOSettings[]? IOSettings { get; set; }
    public Guid? IoDatabase { get; set; }
    public Guid? IoDatabaseSecondary { get; set; }
    public Guid? IoIndexes { get; set; }
    public FileStoreSettings[]? FileStoreSettings { get; set; }
    public Guid? IoBackup { get; set; }
    public Guid? IoLog { get; set; }
    public Guid? AiProvider { get; set; }
    public AiIndexSettings AiSettings { get; set; } = new AiIndexSettings();
    public DatamodelSource[]? DatamodelSources { get; set; }
    public SettingsLocal? LocalSettings { get; set; }
}