namespace Relatude.DB.NodeServer.Settings;
public class NodeStoreContainerSettingsBase {
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool AutoOpen { get; set; }
    public bool WaitUntilOpen { get; set; }
}
public class NodeStoreContainerSettings : NodeStoreContainerSettingsBase {
    public IOSettings[]? IOSettings { get; set; }
    public Guid? IoDatabase { get; set; }
    public Guid? IoDatabaseSecondary { get; set; }
    public Guid? IoIndexes { get; set; }
    public Guid[]? IoFiles { get; set; }
    public Guid? IoBackup { get; set; }
    public Guid? IoLog { get; set; }
    public Guid? AiProvider { get; set; }
    public DatamodelSource[]? DatamodelSources { get; set; }
    public SettingsLocal? LocalSettings { get; set; }
}