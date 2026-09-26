namespace Relatude.DB.SMS;

/// <summary>
/// Sends a text message on behalf of the database, so application code does not hold a gateway
/// account of its own. Reached through <c>NodeStore.SMS</c>.
///
/// <para>Only one implementation exists: <c>RelatudeServicesSMSProvider</c>, which calls the hosted
/// Relatude SMS service and charges each message to the license. The interface is here so that a
/// gateway account of your own can be plugged in later without any calling code changing, the same
/// way <see cref="Relatude.DB.AI.IAIProvider"/> is.</para>
///
/// <para>An implementation is long lived and shared: it is built once when the database is
/// configured and disposed with it, so it must be safe to call from several threads at once.</para>
/// </summary>
public interface ISMSProvider : IDisposable {
    /// <summary>What this provider is, for the admin UI and the log. Not the sender shown on the phone.</summary>
    string Name { get; }

    /// <summary>
    /// Sends one message, or throws saying why it could not be sent. <paramref name="to"/> is any
    /// form of a mobile number - a number without a country code gets the service's default - and
    /// the receipt says which number was actually used.
    /// </summary>
    /// <param name="to">The recipient's mobile number.</param>
    /// <param name="message">The text. What it costs depends on its characters, not only its length: see <see cref="QuoteAsync"/>.</param>
    /// <param name="from">The sender shown on the phone, when the service lets the caller choose one. Null takes the service's own.</param>
    /// <param name="reference">The caller's own label for this message, echoed back and written to the service's log so a message can be traced.</param>
    Task<SmsReceipt> SendAsync(string to, string message, string? from = null, string? reference = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// What a message would cost, without sending it or paying for it. Worth calling on a template
    /// before it goes to everyone: one character outside the GSM alphabet - a curly quote, an en
    /// dash, an emoji - forces the whole text into an encoding where less than half as much fits in
    /// a part, so an innocent edit can double the price.
    /// </summary>
    Task<SmsQuote> QuoteAsync(string to, string message, CancellationToken cancellationToken = default);
}

/// <summary>
/// A message that was accepted. <see cref="Parts"/> is what it cost on the wire - a long or
/// non-GSM text is sent as several messages - and <see cref="Credits"/> what it cost the license.
/// <see cref="MessageId"/> belongs to the service, so a delivery question can be taken to it.
/// </summary>
public sealed record SmsReceipt(string MessageId, string To, int Parts, int Credits, int CreditsLeft, string? Reference = null);

/// <summary>
/// What a message would cost. <see cref="Unicode"/> is true when one character outside the GSM
/// alphabet has pushed the whole text into the shorter encoding, which is the usual reason a short
/// message unexpectedly costs more than one part.
/// </summary>
public sealed record SmsQuote(string To, int Parts, int Credits, bool Unicode, int Characters);

/// <summary>
/// How the database reaches an SMS service. The shape follows
/// <see cref="Relatude.DB.AI.AIProviderSettings"/>: a type name resolved when the database opens,
/// an endpoint, and the key that pays for the calls. The hosted Relatude service needs neither of
/// the last two on a server, which sends with the installation's own license.
/// </summary>
public class SMSProviderSettings {
    /// <summary>Which implementation sends. Empty or "RelatudeServices" is the hosted Relatude SMS service; anything else is taken as the full type name of a custom provider.</summary>
    public string? TypeName { get; set; }

    /// <summary>The root of the service. Empty uses the provider's own default. The settings page only
    /// offers it for a custom provider; a self-hosted Relatude service is set in the settings file.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>The key a custom provider sends with. The Relatude service charges each message to the
    /// installation's license and uses the license's API key, falling back to this one only where the
    /// server has none - or where the provider is built from code, without a server.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The sender shown on the phone when a call names none. Most gateways only allow senders registered with them, so the service may ignore it.</summary>
    public string? From { get; set; }
}
