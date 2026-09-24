using Relatude.DB.NodeServer.Settings;
using Relatude.DB.SMS;

namespace Relatude.DB.NodeServer.UI;

/// <summary>
/// The License page: where this installation stands with Relatude.License, and the keys that put it
/// there. Reading is <see cref="LicenseLogin.DescribeAsync"/>; writing goes through the settings
/// accessor, so a key decided by configuration is refused here exactly as it is on the settings
/// page rather than being written to a file the overlay would win over at the next start.
///
/// <para>Deliberately not cached. It is asked when the page opens and after anything changes, which
/// is rare, and a stale answer about whether a license is valid is worse than a round trip.</para>
/// </summary>
sealed class UILicense(RelatudeDBServer server) {
    /// <summary>The settings the page edits. The API key is secret, so it is written but never read back.</summary>
    static readonly string[] _paths = [
        nameof(RelatudeDBServerSettings.LicenseKey),
        nameof(RelatudeDBServerSettings.ApiKey),
        nameof(RelatudeDBServerSettings.AllowLicenseeAdminLogin),
#if DEBUG
        nameof(RelatudeDBServerSettings.ServicesServerUrl),
#endif
    ];

    /// <summary>
    /// Whether the page shows and edits the license server address. Only a debug build of the server
    /// does, matching the settings catalog: the address is still sent, since the page builds its
    /// portal links from it, but a release build does not put it on screen.
    /// </summary>
#if DEBUG
    const bool _showLicenseServer = true;
#else
    const bool _showLicenseServer = false;
#endif

    internal void Register(UICommands commands) {
        commands.Register("license-status", async ctx => await describe());
        // Getting a license without copying a key by hand: the page opens the claim url in a tab and
        // asks here every couple of seconds until somebody has answered it. The keys come back to the
        // page, which saves them the way it saves any other setting.
        commands.Register("license-pair-start", async ctx => await server.LicenseLogin.StartPairingAsync(ctx.Http.RequestAborted));
        commands.Register("license-pair-poll", async ctx => await server.LicenseLogin.PollPairingAsync(ctx.Payload<PairingPayload>().PairingId ?? "", ctx.Http.RequestAborted));
        commands.Register("license-pair-cancel", async ctx => {
            await server.LicenseLogin.CancelPairingAsync(ctx.Payload<PairingPayload>().PairingId ?? "", ctx.Http.RequestAborted);
            return null;
        });
        commands.Register("license-sms-test", async ctx => await sendTestSms(ctx.Payload<SmsTestPayload>(), ctx.Http.RequestAborted));
    }

    sealed record PairingPayload(string? PairingId);
    sealed record SmsTestPayload(string? From, string? To, string? Message);

    /// <summary>The feature a license must carry for the Relatude SMS service to accept its API key.</summary>
    internal const string SmsFeature = "SMS";

    internal static bool CarriesSms(LicenseLogin.LicenseInfo? license) =>
        license != null && license.Features.Any(f => string.Equals(f?.Trim(), SmsFeature, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Sends one real message through the hosted Relatude SMS service with this installation's own
    /// API key, so whoever set up the license can see it works before any code depends on it. It is
    /// the service, not a database: no database's SMS settings are read, and the message is charged
    /// to the license like any other. Asked of the license server first, so a license without the
    /// feature is told so here rather than by a refusal from the service.
    /// </summary>
    async Task<SmsReceipt> sendTestSms(SmsTestPayload payload, CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(payload.To)) throw new Exception("A recipient number is required.");
        if (string.IsNullOrWhiteSpace(payload.Message)) throw new Exception("A message is required.");
        var status = await server.LicenseLogin.DescribeAsync(cancellationToken);
        if (status.State != "valid" || status.License == null) throw new Exception("There is no valid license to send with. " + status.Reason);
        if (!status.License.Active) throw new Exception("The license is " + (status.License.Expired ? "expired." : "disabled."));
        if (!CarriesSms(status.License)) throw new Exception("This license does not include SMS.");
        using var provider = new RelatudeServicesSMSProvider(new SMSProviderSettings { ApiKey = server.Settings.ApiKey });
        return await provider.SendAsync(payload.To.Trim(), payload.Message, string.IsNullOrWhiteSpace(payload.From) ? null : payload.From.Trim(),
            reference: "admin-ui-test", cancellationToken: cancellationToken);
    }

    async Task<object> describe() {
        var status = await server.LicenseLogin.DescribeAsync();
        return new {
            status.State,
            status.Reason,
            status.ServicesServerUrl,
            ShowLicenseServer = _showLicenseServer,
            status.HasLicenseKey,
            status.HasApiKey,
            status.LicenseKey,
            status.SignInEnabled,
            // reporting in (DisableHeartbeat) is left out on purpose: it is a json-file setting only
            status.LastContactUtc,
            status.License,
            status.Pairing,
            // which of the fields the page offers are decided by configuration, and so cannot be
            // edited here: the same rule the settings page shows, on the same paths
            Locked = _paths.Where(isOverridden).ToArray(),
        };
    }

    bool isOverridden(string path) {
        var overlay = server.ConfigurationOverlay;
        return overlay != null && overlay.IsOverridden(SettingsOverlay.OverridePath(null, path), out _);
    }
}
