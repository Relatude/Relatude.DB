using Relatude.DB.AI;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.DataStores.Files;
using Relatude.DB.FileToText;
using Relatude.DB.Imaging;
using Relatude.DB.SMS;
using Relatude.DB.Translation;
using System.Text.Json.Serialization;

namespace Relatude.DB.NodeServer.Settings;

public class NodeStoreContainerSettingsBase {
    public Guid Id { get; set; }
    public string? Name { get; set; }
    /// <summary>
    /// A short, file-system-safe name for the database, naming the folder the server keeps its settings
    /// files in: relatude.settings/{ShortName}/ holds the definitions of its custom logs (logs/) and GraphQL
    /// endpoints (graphql/) and its datamodel overrides (datamodel.json), which every installation shares.
    /// Empty keeps them directly in relatude.settings, which only one database of an installation can do
    /// (<see cref="DatabaseShortName"/>). Letters, digits, '-' and '_'; unique among the databases. Set it
    /// in relatude.db.json, which every installation has: an installation that has another reads other
    /// settings files than the rest.
    /// </summary>
    public string? ShortName { get; set; }
    public string? Description { get; set; }
    public bool AutoOpen { get; set; }
    public bool WaitUntilOpen { get; set; }
}
/// <summary>
/// One file store of a database. <see cref="SameHashSameFile"/> and <see cref="HashAlgorithm"/> have two
/// defaults: a store made in code, by the admin UI or by the CLI starts with one copy per content and
/// SHA256, while a store read from JSON that does not mention them - a settings file, the overrides file
/// or the configuration section, written before the two existed - keeps MD5 and one copy per upload, as
/// it had. Turning dedupe on for an existing store would change when its files are deleted.
/// </summary>
public class FileStoreSettings : IJsonOnDeserializing {
    public Guid Id { get; set; }
    public Guid IoProviderId { get; set; }
    public int? MultiFileFolderDepth { get; set; }
    public FileStoreEngine StoreType { get; set; } = FileStoreEngine.SingleFile;
    /// <summary>
    /// MultiFile only: keep one copy of any content. An upload whose hash and length match a file the
    /// store already holds is not stored again - the new file value points at the existing copy - so
    /// such files are only deleted by the unreferenced file cleanup, never when a value is removed.
    /// Applies to uploads from when it is turned on; files already stored are left as they are, and
    /// the shared ones stay readable if it is turned off again. On for a new store; absent from JSON
    /// means off (see the class).
    /// </summary>
    public bool SameHashSameFile { get; set; } = true;
    /// <summary>
    /// MultiFile only: the hash computed over every uploaded file, and with <see cref="SameHashSameFile"/>
    /// what decides that two uploads are the same file. SHA256 for a new store, as files with the same
    /// MD5 can be crafted, and on CPUs with SHA instructions it is also the faster of the two; absent
    /// from JSON means MD5 (see the class).
    /// </summary>
    public FileHashAlgorithm HashAlgorithm { get; set; } = FileHashAlgorithm.SHA256;
    // runs after the constructor and before the properties are read, so only the ones the JSON names
    // replace these
    void IJsonOnDeserializing.OnDeserializing() {
        SameHashSameFile = false;
        HashAlgorithm = FileHashAlgorithm.MD5;
    }
    /// <summary>The store a new database gets: one file per upload, one copy per content, SHA256.</summary>
    public static FileStoreSettings CreateMultiFile(Guid ioProviderId) => new() {
        Id = SecureGuid.New(),
        IoProviderId = ioProviderId,
        StoreType = FileStoreEngine.MultiFile,
        MultiFileFolderDepth = 2,
        SameHashSameFile = true,
        HashAlgorithm = FileHashAlgorithm.SHA256,
    };
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
    /// <summary>How this database sends text messages, reached from code as <c>NodeStore.Services.SMS</c>. Null on a database that sends none.</summary>
    public SMSProviderSettings? SMSSettings { get; set; }
    /// <summary>How this database reaches image AI, reached from code as <c>NodeStore.Services.Imaging</c>. Null is the hosted Relatude Imaging service, called with the installation's license.</summary>
    public ImagingProviderSettings? ImagingSettings { get; set; }
    /// <summary>How this database reads the text of files, reached from code as <c>NodeStore.Services.FileToText</c>. Null is the hosted Relatude FileToText service, called with the installation's license.</summary>
    public FileToTextProviderSettings? FileToTextSettings { get; set; }
    /// <summary>How this database translates texts, reached from code as <c>NodeStore.Services.Translation</c>. Null is the hosted Relatude Translation service, called with the installation's license.</summary>
    public TranslationProviderSettings? TranslationSettings { get; set; }
    public DatamodelSource[]? DatamodelSources { get; set; }
    public SettingsLocal? LocalSettings { get; set; }
}