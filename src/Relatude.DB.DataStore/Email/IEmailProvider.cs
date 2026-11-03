namespace Relatude.DB.Email;

/// <summary>
/// Sends e-mail on behalf of the database, so application code does not hold a mail account of its
/// own. Reached through <c>NodeStore.Services.Email</c>.
/// <code>
/// await db.Services.Email.SendAsync("kari@example.com", "Your order", "Thank you for your order.");
///
/// await db.Services.Email.SendAsync(new EmailMessage {
///     From = new EmailAddress("orders@myshop.no", "My Shop"),
///     To = ["kari@example.com"],
///     Bcc = ["archive@myshop.no"],
///     ReplyTo = ["support@myshop.no"],
///     Subject = "Your order 1042",
///     Text = "Thank you for your order.",
///     Html = "&lt;p&gt;Thank you for your order.&lt;/p&gt;&lt;img src=\"cid:logo\"&gt;",
///     Attachments = [new EmailAttachment("order-1042.pdf", pdf, "application/pdf"), EmailAttachment.Inline("logo", png, "image/png")],
///     Tags = ["orders"],
///     Reference = "order-1042",
/// });
/// </code>
///
/// <para>Only one implementation exists: <c>RelatudeServicesEmailProvider</c>, which calls the hosted
/// Relatude Email service and charges every recipient to the license. The interface is here so that
/// a mail account of your own can be plugged in later without any calling code changing, the same
/// way <see cref="Relatude.DB.SMS.ISMSProvider"/> is.</para>
///
/// <para>An implementation is long lived and shared: it is built once when the database is
/// configured and disposed with it, so it must be safe to call from several threads at once.</para>
/// </summary>
public interface IEmailProvider : IDisposable {
    /// <summary>What this provider is, for the admin UI and the log. Not the sender the recipient sees.</summary>
    string Name { get; }

    /// <summary>
    /// Sends one e-mail to every recipient in it - <see cref="EmailMessage.To"/>, <see cref="EmailMessage.Cc"/>
    /// and <see cref="EmailMessage.Bcc"/> together - or throws saying why it could not be sent. Every
    /// address is checked before anything is sent: one that cannot be an address refuses the whole message.
    /// </summary>
    Task<EmailReceipt> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// What an e-mail would cost, without sending it or paying for it: how many recipients it has, how
    /// large it is, and the credits. The message is checked as <see cref="SendAsync(EmailMessage, CancellationToken)"/>
    /// checks it, so a quote also says whether it could be sent at all.
    /// </summary>
    Task<EmailQuote> QuoteAsync(EmailMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a short e-mail to one recipient: the common case of <see cref="SendAsync(EmailMessage, CancellationToken)"/>.
    /// </summary>
    /// <param name="to">The recipient's address, or "Name &lt;address&gt;".</param>
    /// <param name="subject">The subject line.</param>
    /// <param name="text">The plain text of the message. Null when <paramref name="html"/> is all there is.</param>
    /// <param name="html">The message as HTML, shown by mail programs that can; <paramref name="text"/> is what the others show.</param>
    /// <param name="from">The sender, when the provider lets the caller choose one. Null takes the provider's own.</param>
    /// <param name="reference">The caller's own label for this message, echoed back and written to the service's log so a message can be traced.</param>
    /// <param name="cancellationToken"></param>
    Task<EmailReceipt> SendAsync(string to, string subject, string? text, string? html = null, string? from = null, string? reference = null,
        CancellationToken cancellationToken = default)
        => SendAsync(new EmailMessage {
            To = [to],
            Subject = subject,
            Text = text,
            Html = html,
            // built here rather than converted: the conversion from a string would turn a null into an address of null
            From = string.IsNullOrWhiteSpace(from) ? null : new EmailAddress(from),
            Reference = reference,
        }, cancellationToken);
}

/// <summary>
/// An e-mail address, and the name to show with it. Written as a plain string it is taken as it is,
/// so <c>"kari@example.com"</c> and <c>"Kari Nordmann &lt;kari@example.com&gt;"</c> both work wherever
/// an address goes.
/// </summary>
/// <param name="Address">The address, or a whole "Name &lt;address&gt;", which the service reads.</param>
/// <param name="Name">The name the recipient's mail program shows beside the address. Null shows the address alone.</param>
public sealed record EmailAddress(string Address, string? Name = null) {
    public static implicit operator EmailAddress(string address) => new(address);

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}

/// <summary>
/// A file sent with an e-mail. One with a <see cref="ContentId"/> is inline: it is not listed as an
/// attachment, and the HTML shows it with <c>&lt;img src="cid:{ContentId}"&gt;</c>.
/// </summary>
/// <param name="FileName">The name the recipient sees and saves it as.</param>
/// <param name="Content">The file itself.</param>
/// <param name="ContentType">Its media type, such as "application/pdf". Null takes it from the file name's extension.</param>
/// <param name="ContentId">For an inline file, the name the HTML refers to it by. Null for an ordinary attachment.</param>
public sealed record EmailAttachment(string FileName, byte[] Content, string? ContentType = null, string? ContentId = null) {
    /// <summary>Whether the file is shown in the message rather than listed as an attachment.</summary>
    public bool IsInline => !string.IsNullOrWhiteSpace(ContentId);

    /// <summary>A file the HTML shows as <c>&lt;img src="cid:{contentId}"&gt;</c>, rather than an attachment.</summary>
    public static EmailAttachment Inline(string contentId, byte[] content, string? contentType = null, string? fileName = null)
        => new(fileName ?? contentId, content, contentType, contentId);

    /// <summary>A file read from disk, named as it is there unless <paramref name="fileName"/> says otherwise.</summary>
    public static EmailAttachment FromFile(string path, string? contentType = null, string? fileName = null)
        => new(fileName ?? Path.GetFileName(path), File.ReadAllBytes(path), contentType);
}

/// <summary>
/// One e-mail. It needs a subject, at least one recipient in <see cref="To"/>, <see cref="Cc"/> or
/// <see cref="Bcc"/>, and a <see cref="Text"/> or <see cref="Html"/> body - both is best, since mail
/// programs that do not show HTML show the text. Every recipient sees the <see cref="To"/> and
/// <see cref="Cc"/> addresses, as with any e-mail; <see cref="Bcc"/> recipients are hidden from the rest.
/// </summary>
public sealed class EmailMessage {
    /// <summary>
    /// The sender the recipient sees. Null takes the provider's own. The Relatude service only sends
    /// as an address at a domain Relatude has approved for the license - any address at it, since the
    /// approval is for the domain - and refuses any other before anything is sent.
    /// </summary>
    public EmailAddress? From { get; set; }
    public List<EmailAddress> To { get; set; } = [];
    public List<EmailAddress> Cc { get; set; } = [];
    public List<EmailAddress> Bcc { get; set; } = [];
    /// <summary>Where replies go, when not to <see cref="From"/>. Any address will do: it needs no approval.</summary>
    public List<EmailAddress> ReplyTo { get; set; } = [];
    public string Subject { get; set; } = "";
    /// <summary>The message as plain text.</summary>
    public string? Text { get; set; }
    /// <summary>The message as HTML. Inline files in <see cref="Attachments"/> are shown with <c>cid:</c> references.</summary>
    public string? Html { get; set; }
    public List<EmailAttachment> Attachments { get; set; } = [];
    /// <summary>
    /// Headers of your own. The Relatude service takes those that start with "X-", and List-Unsubscribe,
    /// List-Unsubscribe-Post, List-Id, In-Reply-To, References, Auto-Submitted and Precedence; the
    /// addresses, the subject and the rest are set from the message itself.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Labels for the provider's statistics, such as "newsletter" or "receipt". The Relatude service takes at most three.</summary>
    public List<string> Tags { get; set; } = [];
    /// <summary>The caller's own label for this message, echoed back and written to the service's log so a message can be traced.</summary>
    public string? Reference { get; set; }
}

/// <summary>
/// An e-mail the provider took. <see cref="Recipients"/> is how many it goes to, every address in
/// To, Cc and Bcc counted once, which is what it costs: <see cref="Credits"/> is the price charged to
/// the license, and <see cref="CreditsLeft"/> the balance after it. <see cref="MessageId"/> belongs to
/// the service, so a delivery question can be taken to it. <see cref="From"/> is the address it went as.
/// </summary>
public sealed record EmailReceipt(string MessageId, string From, int Recipients, long Bytes, int Credits, int CreditsLeft, string? Reference = null);

/// <summary>What an e-mail would cost: its recipients, its size as the service counts it, and the credits.</summary>
public sealed record EmailQuote(int Recipients, long Bytes, int Credits);

/// <summary>
/// How the database reaches an e-mail service. The shape follows <see cref="Relatude.DB.SMS.SMSProviderSettings"/>:
/// a type name resolved when the database opens, an endpoint, and the key that pays for the messages.
/// The hosted Relatude service needs neither of the last two on a server, which sends with the
/// installation's own license.
/// </summary>
public class EmailProviderSettings {
    /// <summary>Which implementation sends. Empty or "RelatudeServices" is the hosted Relatude Email service; anything else is taken as the full type name of a custom provider.</summary>
    public string? TypeName { get; set; }

    /// <summary>The root of the service. Empty uses the provider's own default. The settings page only
    /// offers it for a custom provider; a self-hosted Relatude service is set in the settings file.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>The key a custom provider sends with. The Relatude service charges each message to the
    /// installation's license and uses the license's API key, falling back to this one only where the
    /// server has none - or where the provider is built from code, without a server.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The sender's address when a message names none. The Relatude service only sends as an address at a domain approved
    /// for the license, and refuses any other; empty sends as the service's own.</summary>
    public string? From { get; set; }

    /// <summary>The name shown beside <see cref="From"/>, such as "My Shop". Only used with it.</summary>
    public string? FromName { get; set; }
}
