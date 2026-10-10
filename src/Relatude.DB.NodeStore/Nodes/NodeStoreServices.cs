using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.FileToText;
using Relatude.DB.Imaging;
using Relatude.DB.SMS;
using Relatude.DB.Translation;

namespace Relatude.DB.Nodes;

/// <summary>
/// The services a database offers application code beside its data, reached as <see cref="NodeStore.Services"/>:
/// <code>
/// var summary = await db.Services.AI.GetCompletionAsync("Summarise in one sentence: " + article.Body);
/// var cutout = await db.Services.Imaging.RemoveBackgroundAsync(photo);
/// var text = await db.Services.FileToText.ExtractTextAsync(bytes, "contract.pdf", ["nb", "en"]);
/// var norwegian = await db.Services.Translation.TranslateAsync("Good morning", "nb");
/// await db.Services.SMS.SendAsync("+4712345678", "Your code is 1234");
/// </code>
/// <para>Except for <see cref="AI"/>, which semantic search uses, nothing inside the database calls them:
/// they are here so application code can use the accounts the database is already configured with - on a
/// server, the installation's own Relatude license - rather than holding vendor accounts of its own.</para>
/// <para>Each one throws when the database has none, so check its <c>Has</c> property first on a path that
/// has to work either way. The providers are owned by the store and disposed with it; a store made by
/// <see cref="NodeStore.Context"/> shares them.</para>
/// </summary>
public sealed class NodeStoreServices {
    readonly IDataStore _datastore;
    readonly ISMSProvider? _sms;
    readonly IImagingProvider? _imaging;
    readonly IFileToTextProvider? _fileToText;
    readonly ITranslationProvider? _translation;

    internal NodeStoreServices(IDataStore datastore, ISMSProvider? sms, IImagingProvider? imaging, IFileToTextProvider? fileToText, ITranslationProvider? translation) {
        _datastore = datastore;
        _sms = sms;
        _imaging = imaging;
        _fileToText = fileToText;
        _translation = translation;
    }

    /// <summary>
    /// The AI engine - embeddings and completions - as configured for this database. Semantic search uses
    /// it by itself; application code can use it for completions and embeddings of its own.
    /// <para>Throws when no AI is configured, so check <see cref="HasAI"/> first on a path that has to work either way.</para>
    /// </summary>
    public AIEngine AI => _datastore.AI;
    /// <summary>Whether an AI engine is configured, and <see cref="AI"/> can therefore be used.</summary>
    public bool HasAI => _datastore.HasAIEngine;

    /// <summary>
    /// Sends text messages, as configured for this database: a confirmation, a one-time code, sent through
    /// the account the database is already configured with rather than a gateway account of its own.
    /// <para>Throws when no SMS provider is configured, so check <see cref="HasSMS"/> first on a path that
    /// has to work either way. The provider is owned by the store, so it is not something to dispose after a message.</para>
    /// </summary>
    public ISMSProvider SMS => _sms ?? throw new Exception("No SMS provider is configured for this database. Set one under Messaging in the admin UI, or as SMSSettings in relatude.db.json. ");
    /// <summary>Whether an SMS provider is configured, and <see cref="SMS"/> can therefore be used.</summary>
    public bool HasSMS => _sms != null;

    /// <summary>
    /// Image AI - creating images, changing them, saying what they show - as configured for this database.
    /// On a server it is always there, calling the hosted Relatude Imaging service with the installation's
    /// license unless the database's ImagingSettings name another.
    /// <para>Throws when no imaging provider was given, so check <see cref="HasImaging"/> first on a path
    /// that has to work either way.</para>
    /// </summary>
    public IImagingProvider Imaging => _imaging ?? throw new Exception("No imaging provider is configured for this database. A database on a server has the Relatude Imaging service; a store built from code is given one in its constructor. ");
    /// <summary>Whether an imaging provider is configured, and <see cref="Imaging"/> can therefore be used.</summary>
    public bool HasImaging => _imaging != null;

    /// <summary>
    /// The text of files - documents, spreadsheets, PDFs, e-mails, and pictures read by OCR - as configured
    /// for this database, for application code to index or show. On a server it is always there, calling the
    /// hosted Relatude FileToText service with the installation's license unless the database's
    /// FileToTextSettings name another.
    /// <para>Throws when no provider was given, so check <see cref="HasFileToText"/> first on a path that has
    /// to work either way.</para>
    /// </summary>
    public IFileToTextProvider FileToText => _fileToText ?? throw new Exception("No file-to-text provider is configured for this database. A database on a server has the Relatude FileToText service; a store built from code is given one in its constructor. ");
    /// <summary>Whether a file-to-text provider is configured, and <see cref="FileToText"/> can therefore be used.</summary>
    public bool HasFileToText => _fileToText != null;

    /// <summary>
    /// Translation - texts translated, and the languages they are in found, many in one call - as configured
    /// for this database. On a server it is always there, calling the hosted Relatude Translation service
    /// with the installation's license unless the database's TranslationSettings name another.
    /// <para>Throws when no translation provider was given, so check <see cref="HasTranslation"/> first on a
    /// path that has to work either way.</para>
    /// </summary>
    public ITranslationProvider Translation => _translation ?? throw new Exception("No translation provider is configured for this database. A database on a server has the Relatude Translation service; a store built from code is given one in its constructor. ");
    /// <summary>Whether a translation provider is configured, and <see cref="Translation"/> can therefore be used.</summary>
    public bool HasTranslation => _translation != null;

    /// <summary>
    /// Empties the answer caches of the Imaging, FileToText and Translation providers that keep one
    /// (see <see cref="ICachingServiceProvider"/>), so every question goes to the service again. The AI
    /// embedding cache is the data store's, and is cleared with it. Done by
    /// <c>NodeStore.MaintenanceAsync(MaintenanceAction.ClearAiCache)</c>.
    /// </summary>
    public void ClearCaches() {
        (_imaging as ICachingServiceProvider)?.ClearCache();
        (_fileToText as ICachingServiceProvider)?.ClearCache();
        (_translation as ICachingServiceProvider)?.ClearCache();
    }

    // the AI engine is the data store's, and goes with it
    internal void DisposeProviders() {
        _sms?.Dispose();
        _imaging?.Dispose();
        _fileToText?.Dispose();
        _translation?.Dispose();
    }
}
