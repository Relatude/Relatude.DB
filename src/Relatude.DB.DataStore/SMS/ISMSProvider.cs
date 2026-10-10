namespace Relatude.DB.SMS;

/// <summary>
/// Sends a text message on behalf of the database, so application code does not hold a gateway
/// account of its own. Reached through <c>NodeStore.Services.SMS</c>.
///
/// <para>Only one implementation exists: <c>RelatudeServicesSMSProvider</c>, which calls the hosted
/// Relatude SMS service and charges each message to the license. The interface is here so that a
/// gateway account of your own can be plugged in later without any calling code changing, the same
/// way <see cref="Relatude.DB.AI.IAIProvider"/> is.</para>
///
/// <para>An implementation is long lived and shared: it is built once when the database is
/// configured and disposed with it, so it must be safe to call from several threads at once.</para>
///
/// <para>Many messages at once go with <see cref="SendBatchAsync"/>: all of them or none, paid for
/// before it returns and sent in the background. Both batch methods have a default that throws
/// <see cref="NotSupportedException"/>, so a provider of your own need not offer them.</para>
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

    /// <summary>
    /// Sends many messages in one call, all of them or none. Every message is checked and the whole
    /// batch is paid for before this returns; the messages are then sent in the background, and
    /// <see cref="GetBatchAsync"/> tells how that goes. One message that cannot be sent - a number
    /// that is not one, an empty or too long text - refuses the whole batch, with every such message
    /// named, and nothing is sent or charged.
    /// <para>A provider that cannot send batches says so with a <see cref="NotSupportedException"/>;
    /// sending the messages one at a time with <see cref="SendAsync"/> instead is not the same thing,
    /// since some of them may then go and some not.</para>
    /// </summary>
    /// <param name="messages">The messages, in the order they should go. Each one's number is any form of a mobile number, as for <see cref="SendAsync"/>.</param>
    /// <param name="from">The sender of every message in the batch. Null takes the provider's own.</param>
    /// <param name="reference">The caller's own label for the batch; a message's own reference labels that message.</param>
    Task<SmsBatchReceipt> SendBatchAsync(IReadOnlyList<SmsBatchMessage> messages, string? from = null, string? reference = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{Name} cannot send a batch of messages.");

    /// <summary>
    /// How a batch sent with <see cref="SendBatchAsync"/> is going: which messages are sent, which are
    /// still waiting, and which could not be. Null when the provider knows no batch by that id.
    /// </summary>
    Task<SmsBatchStatus?> GetBatchAsync(string batchId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{Name} cannot send a batch of messages.");
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

/// <summary>One message of a batch: the recipient, the text, and the caller's own label for it.</summary>
public sealed record SmsBatchMessage(string To, string Message, string? Reference = null);

/// <summary>
/// A batch that was paid for and queued. <see cref="Credits"/> is what the whole batch cost, and
/// <see cref="CreditsLeft"/> the balance after it. <see cref="Items"/> are the messages in the order
/// they were given, each with the number it goes to and its price. The messages are sent after this
/// is returned: ask <see cref="ISMSProvider.GetBatchAsync"/> with <see cref="BatchId"/> how it goes.
/// </summary>
public sealed record SmsBatchReceipt(string BatchId, int Messages, int Parts, int Credits, int CreditsLeft, string From,
    IReadOnlyList<SmsBatchItem> Items, string? Reference = null);

/// <summary>One message of an accepted batch: the number it goes to, as the service will send it, and what it costs.</summary>
public sealed record SmsBatchItem(string To, int Parts, int Credits, string? Reference = null);

/// <summary>
/// How a batch is going. <see cref="State"/> is "queued" until the first message is handed to the
/// gateway, "sending" while any is left, and "done" when every message has an outcome; <see cref="Pending"/>
/// counts the messages without one yet. <see cref="Items"/> are in the order the messages were given.
/// Every message was paid for with the batch, whatever became of it.
/// </summary>
public sealed record SmsBatchStatus(string BatchId, string State, DateTime AcceptedUtc, DateTime? CompletedUtc, string From,
    int Messages, int Credits, int Pending, int Sent, int Undeliverable, int Failed, int Expired,
    IReadOnlyList<SmsBatchMessageStatus> Items, string? Reference = null) {
    /// <summary>Every message has an outcome: nothing more will change.</summary>
    public bool IsDone => State == "done";
}

/// <summary>
/// One message of a batch. <see cref="State"/> is "queued" (waiting its turn), "sending" (with the
/// gateway now), "sent" (the gateway took it, and <see cref="MessageId"/> is its id), "undeliverable"
/// (the gateway will not deliver this message, and <see cref="Error"/> says why), "failed" (the
/// gateway failed while it had the message, so it may or may not have reached the phone) or
/// "expired" (the gateway could not take it in time, and it was not sent).
/// </summary>
public sealed record SmsBatchMessageStatus(string To, int Parts, string State, string? MessageId = null, string? Error = null,
    DateTime? AtUtc = null, string? Reference = null);

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

    /// <summary>The sender shown on the phone when a call names none. The Relatude service only sends as a sender
    /// approved for the license, and refuses any other; most other gateways only allow senders registered with them.</summary>
    public string? From { get; set; }
}
