using Relatude.DB.AI;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.SMS;

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
public class NodeStoreContainerSettings : NodeStoreContainerSettingsBase {
    public IOSettings[]? IOSettings { get; set; }
    public Guid? IoDatabase { get; set; }
    public Guid? IoDatabaseSecondary { get; set; }
    public Guid? IoIndexes { get; set; }
    public FileStoreSettings[]? FileStoreSettings { get; set; }
    public Guid? IoBackup { get; set; }
    public Guid? IoLog { get; set; }
    public AIProviderSettings? AISettings { get; set; }
    /// <summary>How this database sends text messages, reached from code as <c>NodeStore.SMS</c>. Null on a database that sends none.</summary>
    public SMSProviderSettings? SMSSettings { get; set; }
    public DatamodelSource[]? DatamodelSources { get; set; }
    /// <summary>
    /// Where this database keeps its datamodel overrides - the attributes the data model editor sets on
    /// top of what the sources say (see <see cref="Datamodel.Overrides"/>). Empty keeps them with the
    /// database: <c>datamodels/datamodel.overrides.json</c> on its storage provider, beside the editor's
    /// drafts and history, where they survive a redeploy of the site. A path - relative to the folder
    /// holding the settings file unless rooted - keeps them in that file instead, for a team that wants
    /// them in source control and deployed with the site.
    /// </summary>
    public string? DatamodelOverridesPath { get; set; }
    public SettingsLocal? LocalSettings { get; set; }
}