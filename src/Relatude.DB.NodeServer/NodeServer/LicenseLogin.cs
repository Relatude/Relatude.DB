using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relatude.DB.Common;
using Relatude.DB.NodeServer.Settings;
namespace Relatude.DB.NodeServer;
/// <summary>
/// This installation's side of Relatude.License: letting an admin sign in with a Relatude.License
/// account, and reporting in (the heartbeat). Both need an API key from the portal in the settings;
/// the sign-in also needs AllowLicenseeAdminLogin.
/// <para>The API key is the only key anybody has to copy. It belongs to one license for as long as
/// it exists, and the license server says which (<see cref="LookUpAsync"/>), so the license key is
/// found from it rather than trusted from the settings: LicenseKey in the settings is a record of
/// it, used only while the license server has not answered.</para>
/// <para>The sign-in is a redirect in three steps, started by the login page rather than by a link.
/// The page tells this server the address it is open on (<see cref="BeginAsync"/>), and the server
/// takes it only when it is one of its own, by its settings or by an approval in Relatude Services
/// (<see cref="returnAddress"/>). It keeps a random ticket
/// for the sign-in in memory and puts it in the browser's cookie, and tells the license server, over
/// the back channel with its API key, that a browser is about to come, where to send it back, and
/// the hash of a secret only this process holds. The license server answers with a sign-in url,
/// and the browser is sent there with nothing but a request id (the redirect uri never travels
/// through the browser). The user signs in there and the license server checks that they have
/// access to the license. The browser then comes back to the callback with a one-time code. This
/// server takes it only with the ticket of a sign-in it started itself, and trades it, again over
/// the back channel with its API key and the secret, for who the user is - and opens its own
/// session. So a code is of use only in the browser and the server process that started the
/// sign-in, and the master login keeps working throughout.</para>
/// </summary>
public sealed class LicenseLogin(RelatudeDBServer server) : IDisposable {
    // The shapes the license server speaks (Relatude.DB.Services: Connect/ConnectContracts.cs and
    // Licensing/LicensingContracts.cs). Only the fields used here; change both sides together.
    // CodeChallenge is the SHA-256 of CodeVerifier, base64url, as in OAuth's PKCE: the license server
    // hands the user over only to whoever shows the secret behind it, which is the process that asked.
    sealed record LoginRequestCreate(Guid ApiKey, Guid LicenseKey, string InstallationKey, string RedirectUri, string? InstallationName, string CodeChallenge);
    sealed record LoginRequestCreated(string RequestId, string LoginUrl, DateTime ExpiresUtc);
    sealed record TokenRequest(Guid ApiKey, string Code, string State, string CodeVerifier); // State: the request id, so the code is redeemable only for the sign-in it came from
    sealed record TokenResult(Guid Subject, string Name, string Email, string Mobile, Guid LicenseId, string LicenseName, Guid InstallationId, string InstallationKey, string Role, DateTime IssuedUtc, DateTime ExpiresUtc);
    sealed record ApprovedAddressesRequest(Guid ApiKey, string InstallationKey);
    sealed record ApprovedAddressesAnswer(string[]? Addresses); // authorities: the host, plus the port when it is not 443
    sealed record Heartbeat(Guid ApiKey, string InstallationKey, string? Name, int Nodes, string? ServerName, string? MachineName, string? BuildVersion);
    sealed record SmsSenderAnswer(bool Allowed, string? Reason);
    sealed record Refusal(string? Reason);
    /// <summary>The license server's answer to a heartbeat: may this installation keep running, and the soft switches of its license.</summary>
    public sealed record Validity(bool Valid, string? Reason, string MessageToAllEditors = "", string MessageToAllVisitors = "", bool StopEdit = false, bool StopVisit = false);

    /// <summary>What the license carries, as the license server tells an installation holding its API key.</summary>
    /// <param name="SmsSenders">The senders Relatude has approved for the license's text messages, as the
    /// license server writes them: names of up to eleven letters and digits, and phone numbers with their
    /// country code. A message goes as one of these or as the SMS service's own sender, never as anything
    /// else. Null from a license server older than the list, which means none.</param>
    /// <param name="ApiKeyName">What the license's people call the API key the license was asked for
    /// with, so the page can say which of its keys this installation holds. Null for a key without a
    /// name, and from a license server older than the names.</param>
    public sealed record LicenseInfo(
        Guid Id, string Name, bool Disabled, bool Expired, DateTime? ExpiresUtc,
        FeatureInfo[] Features, LimitInfo[] Limits, AccountInfo[] Accounts,
        string MessageToAllEditors, string MessageToAllVisitors, bool StopEdit, bool StopVisit,
        string[]? SmsSenders = null, string? ApiKeyName = null) {
        /// <summary>Neither disabled nor expired: its features, limits and credits are honoured.</summary>
        public bool Active => !Disabled && !Expired;
    }
    /// <summary>A feature the license carries. <c>Key</c> is what code matches on (trimmed, case-insensitive); <c>Name</c> is only for people to read.</summary>
    public sealed record FeatureInfo(string Key, string Name);
    /// <summary>A numeric cap on the license. <c>Key</c> is what code matches on; <c>Name</c> is only for display.</summary>
    public sealed record LimitInfo(string Key, string Name, int MaxValue, bool Unlimited);
    /// <summary>A monthly credit account and its rate limits. <c>Key</c> is what code matches on, "sms" for instance; <c>Name</c> is only for display.</summary>
    public sealed record AccountInfo(string Key, string Name, int MonthlyLimit, int UsedThisMonth, int BalanceLeft, RateWindow Minute, RateWindow Hour, RateWindow Day);
    /// <summary>Credits allowed and spent in one clock window. A limit of zero means no limit.</summary>
    public sealed record RateWindow(int Limit, int Used);

    /// <summary>
    /// How this installation stands with the license server, for the Relatude Services page. One of:
    /// <list type="bullet">
    /// <item><c>missing</c>: no API key is set, so there is nothing to ask about.</item>
    /// <item><c>malformed</c>: the API key is set but is not a key, so the server was not asked.</item>
    /// <item><c>unreachable</c>: the license server did not answer, which says nothing about the license.</item>
    /// <item><c>invalid</c>: the server does not recognise the API key, or will not answer for it.</item>
    /// <item><c>valid</c>: the server answered, and <see cref="License"/> says what it carries - including whether it is disabled or expired.</item>
    /// </list>
    /// <para><see cref="LicenseKey"/> is the key this installation goes by: the license server's answer
    /// for the API key when it gave one, otherwise what the settings say. <see cref="ApiKeyStart"/> is
    /// the first five characters of the API key in the settings - enough to tell which key is saved,
    /// and all of the secret that is ever handed back.</para>
    /// </summary>
    public sealed record LicenseStatus(
        string State, string? Reason, string ServicesServerUrl,
        bool HasLicenseKey, bool HasApiKey, string? ApiKeyStart, string? LicenseKey,
        bool SignInEnabled, bool HeartbeatDisabled, DateTime? LastContactUtc,
        LicenseInfo? License,
        /// <summary>A pairing this server is still waiting on, so a page that has just loaded takes it up rather than starting a second one.</summary>
        PairingHandle? Pairing,
        /// <summary>The key Relatude Services knows this installation by, so it can be found there.</summary>
        InstallationInfo Installation);

    /// <summary>
    /// What the license server says about one API key: <c>valid</c> with the license it belongs to -
    /// whose <see cref="LicenseInfo.Id"/> is the license key - or <c>invalid</c> or <c>unreachable</c>
    /// with the reason, in the same sense as <see cref="LicenseStatus"/>.
    /// </summary>
    public sealed record ApiKeyLookup(string State, string? Reason, LicenseInfo? License);

    static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    HttpClient? _httpClient;
    string? _httpClientFor;
    /// <summary>
    /// One client per license server url, made when the url is first used or changes. A loopback url
    /// is a developer's own test server behind the ASP.NET development certificate, which the machine
    /// does not always trust; that case, and only that case, accepts any certificate - there is nobody
    /// between this process and localhost to impersonate the server.
    /// </summary>
    HttpClient http {
        get {
            var url = baseUrl;
            if (_httpClient != null && _httpClientFor == url) return _httpClient;
            var handler = new HttpClientHandler();
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback)
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            _httpClientFor = url;
            _httpClient = client; // a client replaced mid-flight is left to the GC, so whoever holds it finishes
            return client;
        }
    }
    const string _stateCookie = "RelatudeDBLicenseLogin";
    static readonly TimeSpan _stateLifetime = TimeSpan.FromMinutes(10);
    static readonly TimeSpan _heartbeatFirst = TimeSpan.FromSeconds(20);
    static readonly TimeSpan _heartbeatInterval = TimeSpan.FromMinutes(10);

    RelatudeDBServerSettings settings => server.Settings; // read each time: the keys can be set while the server runs
    Timer? _heartbeat;

    /// <summary>The last heartbeat answer, or null before the first one. Nothing acts on it yet.</summary>
    public Validity? LastValidity { get; private set; }
    public DateTime? LastHeartbeatUtc { get; private set; }

    /// <summary>
    /// The API key is set and is a guid; without it there is nothing to say to the license server.
    /// It is the only key needed: the license key is found from it (<see cref="LookUpAsync"/>).
    /// </summary>
    public bool HasKeys => tryGetApiKey(out _);
    /// <summary>The login page may offer "Sign in with Relatude.License".</summary>
    public bool SignInAvailable => settings.AllowLicenseeAdminLogin && HasKeys;

    bool tryGetApiKey(out Guid apiKey) => Guid.TryParse(settings.ApiKey, out apiKey);

    /// <summary>The license an API key was last found to belong to. An API key never moves to another license, so this holds until the key itself changes.</summary>
    sealed record KnownLicense(Guid ApiKey, Guid LicenseKey);
    volatile KnownLicense? _known; // a reference, so a read never sees half of a write
    string baseUrl => (string.IsNullOrWhiteSpace(settings.ServicesServerUrl) ? Defaults.ServicesServerUrl : settings.ServicesServerUrl).TrimEnd('/');
    /// <summary>
    /// The key Relatude Services knows this installation by, and what it is made of, for the Relatude
    /// Services page: <c>server id:host id:data id</c>.
    /// </summary>
    /// <param name="Key">What the heartbeat, the sign-in and a pairing send.</param>
    /// <param name="ServerId">The Id in relatude.db.json.</param>
    /// <param name="HostId">The fingerprint of the host, see <see cref="InstallationIdentity"/>.</param>
    /// <param name="Host">What <paramref name="HostId"/> is calculated from: the Azure App Service app and slot, or this machine.</param>
    /// <param name="DataId">The id kept with the default database, see <see cref="InstallationDataId"/>; null when it could not be kept.</param>
    /// <param name="DataIdPlace">Where <paramref name="DataId"/> is kept.</param>
    /// <param name="DataIdProblem">Why there is no <paramref name="DataId"/>.</param>
    public sealed record InstallationInfo(string Key, Guid ServerId, string HostId, string Host, string? DataId, string DataIdPlace, string? DataIdProblem);

    readonly InstallationDataId _dataId = new(server);

    /// <summary>
    /// What this installation calls itself towards the license server: the persisted server id, the
    /// host fingerprint and the id kept with the default database, joined. The server id alone travels
    /// with relatude.db.json, so a settings file published to five servers or apps would look like one
    /// installation. The host fingerprint is shared by every application on one machine - on Azure App
    /// Service it is the app and slot instead, which a move to another worker leaves alone - and the
    /// data id tells apart the applications that share both, since each keeps its own data. Together
    /// they count what the license server wants counted: one installation per application and host.
    /// </summary>
    public InstallationInfo DescribeInstallation() {
        var data = _dataId.Get();
        var host = fingerprint.ToLowerInvariant();
        var key = settings.Id.ToString("D") + ":" + host + (data.Id == null ? "" : ":" + data.Id);
        return new InstallationInfo(key, settings.Id, host, InstallationIdentity.Describe(), data.Id, data.Place, data.Problem);
    }
    string installationKey => DescribeInstallation().Key;
    /// <summary>The settings were read again (a soft restart), and may name another default database to keep the data id with.</summary>
    internal void ForgetInstallationDataId() => _dataId.Forget();
    /// <summary>
    /// InstallationIdentity.Get() is deterministic and cached after the first call, but that first call
    /// reads hardware identifiers and may spawn a process (up to three seconds on Windows), so
    /// <see cref="StartHeartbeat"/> warms it up off the request path. On Azure App Service it is read
    /// from the environment and costs nothing.
    /// </summary>
    static string fingerprint => InstallationIdentity.Get();

    /// <summary>What the login page asks before it decides whether to show the button.</summary>
    public object DescribeOptions() => new { Available = SignInAvailable, ServerUrl = SignInAvailable ? baseUrl : null };

    /// <summary>What the login page says as it starts the sign-in: the address it is open on
    /// (<c>location.href</c>), and its colours, see <see cref="colourParameters"/>.</summary>
    public sealed record BeginRequest(string? Url, string? Bg = null, string? Track = null, string? Bar = null);
    /// <summary>Where the login page sends the browser next, or why it cannot, which the page shows. One of the two is set.</summary>
    public sealed record BeginResult(string? LoginUrl, string? Error);

    /// <summary>
    /// A sign-in this server has started and not yet seen come back, kept under the ticket in the
    /// cookie of the browser that started it. The ticket is in no url, and the verifier never leaves
    /// this process but to the license server at the end, which was given only its hash at the start.
    /// Kept in memory on purpose: a sign-in completes on the server process that started it or not at
    /// all - not on another site holding the same keys, and not after a restart.
    /// </summary>
    sealed record PendingSignIn(string RequestId, string CodeVerifier, DateTime ExpiresUtc);
    readonly ConcurrentDictionary<string, PendingSignIn> _pendingSignIns = new();
    /// <summary>Anyone who can open the login page can start a sign-in, so how many may wait at once is capped.</summary>
    const int _maxPendingSignIns = 1000;
    const string _couldNotReach = "Relatude Services could not be reached. Use the master login, or try again later.";

    /// <summary>
    /// Step one, asked by the login page with the address it is open on: register the sign-in with
    /// the license server, keep its ticket here and in this browser's cookie, and answer with where to
    /// send the browser - or why not.
    /// </summary>
    public async Task<BeginResult> BeginAsync(HttpContext context, BeginRequest? begin) {
        if (!SignInAvailable || !tryGetApiKey(out var apiKey)) return refused("Sign-in with Relatude Services is not enabled on this server.");
        var returnTo = returnAddress(context, begin?.Url);
        if (returnTo.Refusal is { } refusal) return refused(refusal);
        // the same question the heartbeat asks, and here it also answers the user faster than
        // waiting out the http timeout would
        if (!await TcpProbe.IsListeningAsync(baseUrl, cancellationToken: context.RequestAborted)) return refused(unreachable("start"));
        var returnBase = returnTo.Base;
        if (returnTo.Check is { } check) {
            var (approved, failure) = await isApprovedAddressAsync(apiKey, check.Authority, context.RequestAborted);
            if (failure != null) return refused(failure);
            if (!approved) return refused(check.Refusal);
            returnBase = check.Base;
        }
        var redirectUri = returnBase + server.ApiUrlPublic + "license-login/callback/";
        // The license key goes with the API key, and the license server only takes the one the API
        // key belongs to - so it is that one, found from the API key the first time it is needed.
        if (_known is not { } known || known.ApiKey != apiKey) {
            var lookup = await LookUpAsync(apiKey, context.RequestAborted);
            if (lookup.License == null) {
                RelatudeDBServer.Trace("Sign-in with Relatude.License could not start: the API key was not looked up. " + lookup.Reason);
                return refused(lookup.State == "invalid" ? "Relatude Services does not accept this server's API key: " + lookup.Reason : _couldNotReach);
            }
            known = new KnownLicense(apiKey, lookup.License.Id);
        }
        forgetExpiredSignIns();
        if (_pendingSignIns.Count >= _maxPendingSignIns) {
            RelatudeDBServer.Trace("Sign-in with Relatude.License refused a start: " + _maxPendingSignIns + " sign-ins are already waiting to come back.");
            return refused("Too many sign-ins are under way on this server. Try again in a few minutes.");
        }
        var ticket = randomToken();
        var verifier = randomToken();
        var request = new LoginRequestCreate(apiKey, known.LicenseKey, installationKey, redirectUri, settings.Name, challengeOf(verifier));
        try {
            using var response = await http.PostAsJsonAsync(baseUrl + "/api/connect/login-requests", request, _json, context.RequestAborted);
            if (!response.IsSuccessStatusCode) return refused("Relatude Services refused the sign-in: " + await reasonOf(response));
            var created = await response.Content.ReadFromJsonAsync<LoginRequestCreated>(_json, context.RequestAborted);
            if (created == null || string.IsNullOrEmpty(created.LoginUrl) || string.IsNullOrEmpty(created.RequestId)) return refused("Relatude Services answered without a sign-in url.");
            _pendingSignIns[ticket] = new PendingSignIn(created.RequestId, verifier, DateTime.UtcNow + _stateLifetime);
            // binds the browser that leaves to the one that comes back with the code, and both to this process
            context.Response.Cookies.Append(_stateCookie, ticket, stateCookieOptions(_stateLifetime));
            return new BeginResult(withQuery(created.LoginUrl, colourParameters(begin)), null);
        } catch (Exception err) when (err is HttpRequestException or TaskCanceledException or JsonException) {
            RelatudeDBServer.Trace("Sign-in with Relatude.License could not start: " + err.Message);
            return refused(_couldNotReach);
        }
    }

    static BeginResult refused(string reason) => new(null, reason);

    // The callback writes its redirect straight to the response rather than returning an IResult.
    // A lambda of the shape (HttpContext) => Task<IResult> is also a RequestDelegate, and MapGet
    // prefers that overload: the task is awaited and its result silently dropped, so the browser
    // would get an empty 200 instead of the redirect. Writing the response directly is correct
    // whichever overload the mapper ends up with.

    /// <summary>
    /// Step three: the browser is back with a code. It is taken only with the ticket of a sign-in this
    /// process started, and only once; then traded for the user, and the session opened.
    /// </summary>
    public async Task CallbackAsync(HttpContext context, string? code, string? state) {
        var ticket = context.Request.Cookies[_stateCookie];
        context.Response.Cookies.Delete(_stateCookie, stateCookieOptions(null));
        // one use: the sign-in is taken out whatever happens next
        PendingSignIn? pending = null;
        if (!string.IsNullOrEmpty(ticket)) _pendingSignIns.TryRemove(ticket, out pending);
        if (!SignInAvailable || !tryGetApiKey(out var apiKey)) { failed(context, "Sign-in with Relatude Services is not enabled on this server."); return; }
        if (pending == null || pending.ExpiresUtc < DateTime.UtcNow) { failed(context, "This sign-in was not started here, or it has expired. Try again."); return; }
        if (string.IsNullOrEmpty(code) || state != pending.RequestId) { failed(context, "The sign-in did not come back the way it left. Try again."); return; }
        if (!await TcpProbe.IsListeningAsync(baseUrl, cancellationToken: context.RequestAborted)) { failed(context, unreachable("complete")); return; }
        try {
            var request = new TokenRequest(apiKey, code, pending.RequestId, pending.CodeVerifier);
            using var response = await http.PostAsJsonAsync(baseUrl + "/api/connect/token", request, _json, context.RequestAborted);
            if (!response.IsSuccessStatusCode) { failed(context, "Relatude Services refused the sign-in: " + await reasonOf(response)); return; }
            var token = await response.Content.ReadFromJsonAsync<TokenResult>(_json, context.RequestAborted);
            if (token == null || token.Subject == Guid.Empty) { failed(context, "Relatude Services answered without a user."); return; }
            if (!string.Equals(token.InstallationKey, installationKey, StringComparison.OrdinalIgnoreCase)) { failed(context, "The sign-in was meant for another installation."); return; }
            server.Authentication.LogInLicensee(context, token.Subject.ToString(), token.Name, token.ExpiresUtc);
            RelatudeDBServer.Trace($"{token.Name} signed in with Relatude.License as {token.Role} of the license \"{token.LicenseName}\".");
            context.Response.Redirect(server.ApiUrlRoot + "/");
        } catch (Exception err) when (err is HttpRequestException or TaskCanceledException or JsonException) {
            RelatudeDBServer.Trace("Sign-in with Relatude.License could not complete: " + err.Message);
            failed(context, _couldNotReach);
        }
    }

    void forgetExpiredSignIns() {
        var now = DateTime.UtcNow;
        foreach (var (ticket, pending) in _pendingSignIns) if (pending.ExpiresUtc < now) _pendingSignIns.TryRemove(ticket, out _);
    }

    static string randomToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>OAuth's S256 code challenge: the SHA-256 of the verifier, base64url without padding.</summary>
    static string challengeOf(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>
    /// Where the browser comes back to, as <see cref="returnAddress"/> decides it: the base to build the
    /// callback url on, why the sign-in cannot start, or - for an address this server's own settings do
    /// not know - what to ask Relatude Services about it. One is set.
    /// </summary>
    sealed record ReturnAddress(string? Base, string? Refusal, ApprovalCheck? Check = null);
    /// <summary>An https address the settings do not list: its authority as Relatude Services writes approved
    /// hosts, the base to come back to if it is approved there, and the refusal if it is not.</summary>
    sealed record ApprovalCheck(string Authority, string Base, string Refusal);

    /// <summary>
    /// Where the license server sends the browser back to with the code: the address the login page
    /// says it is open on, once it is found to be one of this server's own - one listed in
    /// <see cref="RelatudeDBServerSettings.PublicUrl"/>, a loopback address the request came in on, or
    /// one approved for this installation in Relatude Services (<see cref="isApprovedAddressAsync"/>),
    /// which is what the <see cref="ReturnAddress.Check"/> of the answer asks.
    /// <para>What the page says is checked rather than trusted, because whoever sends the request
    /// chooses it, as they choose the host name in it. A server that returned to any address it was
    /// told would register a sign-in that comes back to an address of the sender's choosing. Should
    /// someone with access to the license then approve that address on the sign-in page, their code
    /// would go to the sender, who could bring it here with the ticket of the sign-in they started, and
    /// be signed in as them. A code sent to a loopback address reaches nobody but the machine it is on.</para>
    /// <para>The page's address rather than the request's host name, because it is the one in the
    /// browser's address bar: behind a proxy that hands this server another host name, only the page's
    /// is where the browser can come back to, and where the ticket's cookie was set. A browser also
    /// says where a request comes from in its Origin header, which no page script can change; when there
    /// is one, it has to agree.</para>
    /// <para>PublicUrl may hold several addresses: one server, or several sharing one settings file,
    /// reached on more than one name. The page's address picks among them, so a sign-in returns to the
    /// address it was started on. It fills itself in: every https address a signed-in admin uses is
    /// added to it (<see cref="RememberPublicUrl"/>). An https address that is not listed is asked about
    /// in Relatude Services, where someone with access to the license may have approved it for this
    /// installation; that answer comes from the license server over the back channel, never from the
    /// request. One that is neither is refused, saying how to add it - it is never sent on to another
    /// site, and the license server is never asked to send a code there.</para>
    /// </summary>
    ReturnAddress returnAddress(HttpContext context, string? pageUrl) {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || (page.Scheme != Uri.UriSchemeHttps && page.Scheme != Uri.UriSchemeHttp))
            return new(null, "The login page did not say which address it is open on. Reload it and try again.");
        var origin = page.GetLeftPart(UriPartial.Authority);
        var sentFrom = context.Request.Headers.Origin.ToString();
        if (sentFrom.Length > 0 && !string.Equals(sentFrom, origin, StringComparison.OrdinalIgnoreCase))
            return new(null, "The sign-in was started from another address than the login page is open on. Reload it and try again.");
        if (page.IsLoopback && isLoopback(context.Request)) return new(origin, null);
        var addresses = new List<PublicAddress>();
        foreach (var address in publicUrlsIn(settings.PublicUrl)) {
            if (!tryPublicBase(address, out var publicUri, out var publicBase))
                return new(null, "The public address " + address + " in the settings (PublicUrl) is not one the sign-in can return to: each address has to be an https address such as https://db.example.com. Fix it in the settings, or use the master login.");
            addresses.Add(new(publicUri, publicBase));
        }
        var match = addresses.FirstOrDefault(a => a.Uri.Scheme == page.Scheme && a.Uri.Port == page.Port && string.Equals(a.Uri.Host, page.Host, StringComparison.OrdinalIgnoreCase));
        if (match != null) return new(match.Base, null);
        if (page.Scheme != Uri.UriSchemeHttps) {
            // the license server sends a browser back only to https, or to http on this machine
            return new(null, "Sign-in with Relatude Services comes back only to an https address, and " + origin + " is not one. "
                + "Open the admin UI on its https address" + (addresses.Count > 0 ? ", " + addresses[0].Base + "," : "") + " or use the master login.");
        }
        // a loopback page asked for from elsewhere: no address in Relatude Services can vouch for that
        if (page.IsLoopback) return new(null, notOurs(origin, addresses));
        // Behind a proxy that adds a path in front, the browser's path to the admin UI is longer than
        // this server's own, but the base is taken from the request rather than from the page all the
        // same: the authority is what was approved, and the path after it is the sender's to choose.
        return new(null, null, new(authorityOf(page), origin + context.Request.PathBase.Value, notOurs(origin, addresses)));
    }

    /// <summary>
    /// Why the sign-in cannot come back to <paramref name="origin"/>, and the ways to change that - the
    /// master login only where it can be used from another machine, since that is where this page is.
    /// </summary>
    string notOurs(string origin, List<PublicAddress> addresses) {
        var masterLogin = settings.AllowMasterLoginOutsideLocalhost && !string.IsNullOrEmpty(settings.MasterUserName) && !string.IsNullOrEmpty(settings.MasterPassword);
        return origin + " is not one of this server's public addresses, so sign-in with Relatude Services cannot come back here. "
            + (addresses.Count > 0 ? "Sign in on " + addresses[0].Base + " instead, or add this one: " : "To add it, ")
            + "approve it for this installation under Installations on the license's page in Relatude Services, "
            + (masterLogin ? "sign in here once with the master login, " : "")
            + "or list it in the public addresses (PublicUrl) in the settings.";
    }

    /// <summary>An address the way Relatude Services writes an approved host: the host in lower case, and the port when it is not the scheme's own.</summary>
    static string authorityOf(Uri uri) => uri.IsDefaultPort ? uri.Host.ToLowerInvariant() : uri.Host.ToLowerInvariant() + ":" + uri.Port;

    /// <summary>One of the addresses in PublicUrl, parsed, and written the way the callback url is built on.</summary>
    sealed record PublicAddress(Uri Uri, string Base);

    /// <summary>The addresses in <see cref="RelatudeDBServerSettings.PublicUrl"/>, which separates them
    /// with commas - semicolons and white space are taken as well, so a list written either way works.</summary>
    static string[] publicUrlsIn(string? value) => string.IsNullOrWhiteSpace(value) ? []
        : value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The addresses approved for this installation in Relatude Services, as last asked, for one API key and installation key.</summary>
    sealed record ApprovedAddresses(Guid ApiKey, string InstallationKey, string[] Authorities, DateTime AskedUtc);
    volatile ApprovedAddresses? _approvedAddresses;
    /// <summary>How long an address found approved is taken as approved without asking again.</summary>
    static readonly TimeSpan _approvedAddressLifetime = TimeSpan.FromSeconds(30);
    /// <summary>How long an address found not approved is refused without asking again, which is what
    /// keeps anyone who can open the login page from turning every request into one to the license server.</summary>
    static readonly TimeSpan _approvedAddressRecheck = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Whether <paramref name="authority"/> is approved for this installation in Relatude Services, or
    /// why that could not be found out. Someone with access to the license approves an address in the
    /// portal, or on the sign-in page of a sign-in started on an address this server knows - so it is
    /// people with access to the license who vouch for it, not whoever sent the request naming it. A
    /// license server from before the question was asked answers none.
    /// </summary>
    async Task<(bool Approved, string? Failure)> isApprovedAddressAsync(Guid apiKey, string authority, CancellationToken cancellationToken) {
        var key = installationKey;
        bool listed(ApprovedAddresses known) => known.Authorities.Any(a => string.Equals(a, authority, StringComparison.OrdinalIgnoreCase));
        if (_approvedAddresses is { } cached && cached.ApiKey == apiKey && cached.InstallationKey == key) {
            var age = DateTime.UtcNow - cached.AskedUtc;
            if (age < _approvedAddressLifetime && listed(cached)) return (true, null);
            if (age < _approvedAddressRecheck) return (false, null);
        }
        try {
            using var response = await http.PostAsJsonAsync(baseUrl + "/api/connect/approved-addresses", new ApprovedAddressesRequest(apiKey, key), _json, cancellationToken);
            string[] authorities;
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) authorities = []; // a license server that does not know the question
            else if (!response.IsSuccessStatusCode) return (false, "Relatude Services refused the sign-in: " + await reasonOf(response));
            else authorities = (await response.Content.ReadFromJsonAsync<ApprovedAddressesAnswer>(_json, cancellationToken))?.Addresses ?? [];
            var known = new ApprovedAddresses(apiKey, key, authorities, DateTime.UtcNow);
            _approvedAddresses = known;
            return (listed(known), null);
        } catch (Exception err) when (err is HttpRequestException or TaskCanceledException or JsonException) {
            RelatudeDBServer.Trace("Sign-in with Relatude.License could not ask which addresses are approved: " + err.Message);
            return (false, _couldNotReach);
        }
    }

    /// <summary>
    /// Adds the address this request came in on to <see cref="RelatudeDBServerSettings.PublicUrl"/>,
    /// unless it is there already. Asked only for requests from someone signed in to this admin UI - a
    /// master login, a session opening the admin UI, the API key saved or the installation paired on the
    /// Relatude Services page - the one kind of request whose host name can be taken at its word: it was
    /// sent by the admin's own browser, which names the address it is on, and a page elsewhere cannot
    /// make it name another. So the public addresses fill themselves in, one for each address the admin
    /// UI is used on. A loopback address is not remembered: it is right only on this machine, where the
    /// sign-in needs no public address. Nor is one that is not https, and a value that configuration
    /// decides is left alone. Nothing here throws: the settings file not being writable leaves the
    /// address remembered until the next start, and says so.
    /// </summary>
    public void RememberPublicUrl(HttpContext context) {
        var seen = context.Request.Scheme + "://" + context.Request.Host.Value + context.Request.PathBase.Value;
        if (!tryPublicBase(seen, out var uri, out var publicBase) || uri.IsLoopback) return;
        if (isListed(settings.PublicUrl, uri)) return; // the usual case, answered without the lock
        lock (_rememberLock) {
            var current = settings.PublicUrl;
            if (isListed(current, uri)) return;
            if (server.DecidedOutsideTheSettingsFiles(nameof(RelatudeDBServerSettings.PublicUrl)) != null) return;
            settings.PublicUrl = string.IsNullOrWhiteSpace(current) ? publicBase : current.Trim() + ", " + publicBase;
            try {
                server.UpdateWAFServerSettingsFile();
            } catch (Exception err) {
                RelatudeDBServer.Trace("The public address " + publicBase + " could not be saved to the settings (" + err.Message + "). It is used until the server restarts.");
            }
            RelatudeDBServer.Trace("Sign-in with Relatude.License may now send browsers back to " + publicBase + ", an address the admin UI was used on by a signed-in admin. "
                + "Remove it from PublicUrl in the settings if it is not one of this server's public addresses.");
        }
    }
    readonly object _rememberLock = new();

    /// <summary>Whether PublicUrl lists an address with the scheme, host and port of <paramref name="uri"/> - what a sign-in started on it would be matched by.</summary>
    static bool isListed(string? publicUrl, Uri uri) =>
        publicUrlsIn(publicUrl).Any(a => tryPublicBase(a, out var listed, out _)
            && listed.Scheme == uri.Scheme && listed.Port == uri.Port && string.Equals(listed.Host, uri.Host, StringComparison.OrdinalIgnoreCase));

    /// <summary>A public address as the sign-in can use it: absolute, https - or http on a loopback host - with no query, fragment or user name, written without a trailing slash.</summary>
    static bool tryPublicBase(string? value, out Uri uri, out string publicBase) {
        uri = null!;
        publicBase = "";
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed)) return false;
        var https = parsed.Scheme == Uri.UriSchemeHttps;
        var localHttp = parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback;
        if (!https && !localHttp) return false;
        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0 || parsed.UserInfo.Length > 0) return false;
        uri = parsed;
        publicBase = parsed.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    static bool isLoopback(HttpRequest request) =>
        request.Host.HasValue && Uri.TryCreate("https://" + request.Host.Value, UriKind.Absolute, out var uri) && uri.IsLoopback;

    /// <summary>
    /// The license server is not answering at all. Said in the same words whether the probe or the
    /// request found out, so which of them noticed is not something the person signing in has to
    /// wonder about; the trace says which, for whoever reads the log.
    /// </summary>
    string unreachable(string step) {
        RelatudeDBServer.Trace($"Sign-in with Relatude.License could not {step}: nothing is listening at {baseUrl}.");
        return _couldNotReach;
    }

    /// <summary>
    /// The colours of the login screen the sign-in was started from (Login.tsx sends them), passed on
    /// to the license server's landing page. When the user can go straight back, that page shows
    /// nothing but a progress line - in these colours it reads as the same screen, not a stop at
    /// another site in between. Only plain hex colours are passed on; anything else is dropped.
    /// </summary>
    static string colourParameters(BeginRequest? begin) {
        var parts = new List<string>();
        foreach (var (name, value) in new[] { ("bg", begin?.Bg), ("track", begin?.Track), ("bar", begin?.Bar) }) {
            if (value != null && isHexColour(value)) parts.Add(name + "=" + Uri.EscapeDataString(value));
        }
        return string.Join("&", parts);
    }
    static bool isHexColour(string value) =>
        value.Length is 4 or 5 or 7 or 9 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
    static string withQuery(string url, string query) =>
        query.Length == 0 ? url : url + (url.Contains('?') ? "&" : "?") + query;

    /// <summary>Back to the login page with the reason, which it shows (Login.tsx reads login-error).</summary>
    void failed(HttpContext context, string reason) =>
        context.Response.Redirect(server.ApiUrlRoot + "/?login-error=" + Uri.EscapeDataString(reason));

    CookieOptions stateCookieOptions(TimeSpan? maxAge) => new() {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax, // sent on the top-level redirect back from the license server, which is what it is for
        MaxAge = maxAge,
        Path = server.ApiUrlPublic.TrimEnd('/'),
    };

    static async Task<string> reasonOf(HttpResponseMessage response) {
        try {
            var refusal = await response.Content.ReadFromJsonAsync<Refusal>(_json);
            if (!string.IsNullOrWhiteSpace(refusal?.Reason)) return refusal.Reason;
        } catch (JsonException) { }
        return "HTTP " + (int)response.StatusCode;
    }

    // ------------------------------------------------------------------ what the Relatude Services page shows

    /// <summary>
    /// How this installation stands with the license server, asked fresh. The API key is read from
    /// the settings, and when it is usable the license server is asked which license it belongs to
    /// and what that license carries.
    ///
    /// <para>Nothing here throws: every way this can fail is one of the states, because the License
    /// page has to say which one it is. "Unreachable" is kept apart from "invalid" on purpose - a
    /// license server that did not answer says nothing about the license, and telling a customer
    /// their license is bad because a network was down would be its own kind of wrong.</para>
    /// </summary>
    public async Task<LicenseStatus> DescribeAsync(CancellationToken cancellationToken = default) {
        var savedLicenseKey = string.IsNullOrWhiteSpace(settings.LicenseKey) ? null : settings.LicenseKey.Trim();
        var hasApiKey = !string.IsNullOrWhiteSpace(settings.ApiKey);
        var apiKeyStart = startOf(settings.ApiKey);
        var installation = DescribeInstallation();
        // an answer for the API key decides the license key, whatever the settings say
        LicenseStatus state(string name, string? reason, LicenseInfo? license = null) => new(
            name, reason, baseUrl, savedLicenseKey != null, hasApiKey, apiKeyStart, license?.Id.ToString("D") ?? savedLicenseKey,
            settings.AllowLicenseeAdminLogin, settings.DisableHeartbeat, LastHeartbeatUtc, license, PendingPairing, installation);

        if (!hasApiKey) return state("missing", savedLicenseKey == null ? "No API key is set." : "A license key is set, but no API key.");
        if (!tryGetApiKey(out var apiKey)) return state("malformed", "The API key is not a guid, so the license server has not been asked.");
        var lookup = await LookUpAsync(apiKey, cancellationToken);
        return state(lookup.State, lookup.Reason, lookup.License);
    }

    /// <summary>
    /// The first five characters of an API key, written the one way a guid is written, so a key saved
    /// in upper case or with braces starts the same as it does in the portal. A value too short to
    /// keep anything back gets nothing.
    /// </summary>
    static string? startOf(string? apiKey) {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        var text = Guid.TryParse(apiKey, out var key) ? key.ToString("D") : apiKey.Trim();
        return text.Length > 10 ? text[..5] : null;
    }

    /// <summary>
    /// Asks the license server which license an API key belongs to, and what that license carries.
    /// This is how the license key is found from the API key: it is the answer's
    /// <see cref="LicenseInfo.Id"/>, and it is remembered for the sign-in, which has to present it.
    /// Any API key can be asked about, not only the one in the settings - the Relatude Services page checks a
    /// pasted key this way before it saves it. Nothing here throws: a failure is in the answer.
    /// </summary>
    public async Task<ApiKeyLookup> LookUpAsync(Guid apiKey, CancellationToken cancellationToken = default) {
        // the same courtesy the heartbeat does itself: a license server that is not running is found
        // out from the socket rather than from a dozen exceptions inside HttpClient
        if (!await TcpProbe.IsListeningAsync(baseUrl, cancellationToken: cancellationToken)) {
            return new ApiKeyLookup("unreachable", "Nothing is listening at " + baseUrl + ".", null);
        }
        try {
            using var response = await http.GetAsync(baseUrl + "/api/license?apiKey=" + apiKey.ToString("D"), cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) {
                // the license server's own words: an unknown, disabled or expired key, or one
                // belonging to no license. All of them mean the same thing here - fix it there.
                return new ApiKeyLookup("invalid", await reasonOf(response), null);
            }
            if (!response.IsSuccessStatusCode) return new ApiKeyLookup("unreachable", "The license server answered " + (int)response.StatusCode + ".", null);
            var license = await response.Content.ReadFromJsonAsync<LicenseInfo>(_json, cancellationToken);
            if (license == null || license.Id == Guid.Empty) return new ApiKeyLookup("unreachable", "The license server answered without a license.", null);
            _known = new KnownLicense(apiKey, license.Id);
            return new ApiKeyLookup("valid", null, license);
        } catch (Exception err) when (err is HttpRequestException or TaskCanceledException or JsonException) {
            return new ApiKeyLookup("unreachable", err.Message, null);
        }
    }

    // ------------------------------------------------------------------ SMS senders

    /// <summary>The senders found allowed lately, by API key and sender, with when each answer runs out.</summary>
    readonly ConcurrentDictionary<(Guid ApiKey, string Sender), DateTime> _allowedSenders = new();
    static readonly TimeSpan _allowedSenderLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Asks the license server whether this installation's license may send text messages as
    /// <paramref name="sender"/>, before a message goes out as it: null when it may, otherwise why not,
    /// in words meant for whoever reads the error. Relatude approves senders license by license; the
    /// customer asks for them on the license's page in Relatude Services. The sender is passed on as
    /// given, only trimmed, since the license server writes numbers and names its own way before it compares them. No
    /// sender at all is the SMS service's own, which every license may use, and is not asked about.
    /// <para>The SMS service asks the license server the same question again before it sends, so this
    /// is the first of two checks rather than the only one; it is what lets the database refuse a
    /// sender with the license server's reason before anything is sent or charged. Nothing here
    /// allows a sender it could not check: without an API key, or with a license server that cannot
    /// be reached or answers something unexpected, the answer is a refusal saying so.</para>
    /// <para>A sender found allowed is not asked about again for a minute, per API key, so a busy site
    /// does not ask before every message; a refusal is never remembered, so a sender approved a moment
    /// later can be used at once.</para>
    /// </summary>
    public async Task<string?> CheckSmsSenderAsync(string sender, CancellationToken cancellationToken = default) {
        var asked = sender?.Trim() ?? "";
        if (asked.Length == 0) return null;
        if (!tryGetApiKey(out var apiKey)) {
            return $"The sender {asked} could not be checked with Relatude Services: this installation has no API key. "
                + "Set one on the Relatude Services page of the admin UI, or leave the sender empty to send as the SMS service's own.";
        }
        var key = (apiKey, asked);
        if (_allowedSenders.TryGetValue(key, out var until) && until > DateTime.UtcNow) return null;
        string notChecked(string why) => $"The sender {asked} could not be checked with Relatude Services, so nothing was sent: {why}";
        if (!await TcpProbe.IsListeningAsync(baseUrl, cancellationToken: cancellationToken)) return notChecked("nothing is listening at " + baseUrl + ".");
        try {
            var url = baseUrl + "/api/license/sms-sender?apiKey=" + apiKey.ToString("D") + "&sender=" + Uri.EscapeDataString(asked);
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return notChecked("the license server answered " + (int)response.StatusCode + ".");
            var answer = await response.Content.ReadFromJsonAsync<SmsSenderAnswer>(_json, cancellationToken);
            if (answer == null) return notChecked("the license server answered with nothing.");
            if (!answer.Allowed) {
                _allowedSenders.TryRemove(key, out _);
                return string.IsNullOrWhiteSpace(answer.Reason) ? $"The sender {asked} is not approved for this license." : answer.Reason;
            }
            rememberAllowed(key);
            return null;
        } catch (Exception err) when ((err is HttpRequestException or TaskCanceledException or JsonException) && !cancellationToken.IsCancellationRequested) {
            return notChecked(err.Message);
        }
    }

    void rememberAllowed((Guid ApiKey, string Sender) key) {
        var now = DateTime.UtcNow;
        // the senders of one license are few, so this only matters to a key that changes often
        if (_allowedSenders.Count > 256) {
            foreach (var entry in _allowedSenders) if (entry.Value <= now) _allowedSenders.TryRemove(entry.Key, out _);
        }
        _allowedSenders[key] = now + _allowedSenderLifetime;
    }

    // ------------------------------------------------------------------ getting a license at all

    // The pairing shapes, mirroring Relatude.DB.Services Connect/ConnectContracts.cs.
    sealed record PairingStart(string? InstallationKey, string? InstallationName);
    sealed record PairingCreated(string PairingId, string Secret, string ClaimUrl, DateTime ExpiresUtc, int PollSeconds);
    sealed record PairingResult(string Status, Guid? LicenseKey, Guid? ApiKey);

    /// <summary>What the admin UI is told to do about a pairing it just started.</summary>
    public sealed record PairingHandle(string PairingId, string ClaimUrl, DateTime ExpiresUtc, int PollSeconds);
    /// <summary>A poll: <c>pending</c>, <c>ready</c> with the keys, <c>expired</c>, or <c>unreachable</c>.</summary>
    public sealed record PairingAnswer(string Status, string? LicenseKey, string? ApiKey, string? Reason);

    // The secret never leaves this process: the browser is given the id, which is in the claim url
    // anyway, and this server keeps the one thing that collects the keys. A pairing at a time is
    // enough - it is one person at one admin UI clicking one button.
    //
    // It is kept here rather than in the page so that a pairing survives the page: going off to the
    // portal in another tab and coming back, or reloading, is exactly what someone does in the
    // middle of this, and it would be a poor flow that forgot what it was waiting for when they did.
    (PairingHandle Handle, string Secret)? _pairing;

    /// <summary>The pairing this server is waiting on, if any, so a page that has just loaded can pick up where the last one left off.</summary>
    public PairingHandle? PendingPairing => _pairing is { } pairing && pairing.Handle.ExpiresUtc > DateTime.UtcNow ? pairing.Handle : null;

    /// <summary>
    /// Asks the license server for a pairing and returns where to send the browser. No keys are
    /// needed, and none are held: that is the point of the flow.
    /// </summary>
    public async Task<PairingHandle> StartPairingAsync(CancellationToken cancellationToken = default) {
        var body = new PairingStart(installationKey, settings.Name);
        using var response = await http.PostAsJsonAsync(baseUrl + "/api/connect/pairings", body, _json, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new Exception("The license server would not start a pairing: " + await reasonOf(response));
        var created = await response.Content.ReadFromJsonAsync<PairingCreated>(_json, cancellationToken)
            ?? throw new Exception("The license server started a pairing but did not say where to go.");
        var handle = new PairingHandle(created.PairingId, created.ClaimUrl, created.ExpiresUtc, created.PollSeconds);
        _pairing = (handle, created.Secret);
        RelatudeDBServer.Trace("Started a license pairing with " + baseUrl + ".");
        return handle;
    }

    /// <summary>
    /// Asks once whether somebody has answered the pairing. The keys are handed back to the admin UI
    /// rather than written here: they are ordinary settings, and saving them goes the way every other
    /// setting does, so a configuration override is honoured and the settings file is written once.
    /// </summary>
    public async Task<PairingAnswer> PollPairingAsync(string pairingId, CancellationToken cancellationToken = default) {
        if (_pairing is not { } pairing || pairing.Handle.PairingId != pairingId) {
            return new PairingAnswer("expired", null, null, "This server is not waiting for that pairing any more.");
        }
        try {
            var url = baseUrl + "/api/connect/pairings/" + Uri.EscapeDataString(pairing.Handle.PairingId) + "/result?secret=" + Uri.EscapeDataString(pairing.Secret);
            using var response = await http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return new PairingAnswer("unreachable", null, null, "The license server answered " + (int)response.StatusCode + ".");
            var result = await response.Content.ReadFromJsonAsync<PairingResult>(_json, cancellationToken);
            if (result == null) return new PairingAnswer("unreachable", null, null, "The license server answered with nothing.");
            if (result.Status == "ready" && result.LicenseKey is { } license && result.ApiKey is { } apiKey) {
                _pairing = null; // spent at both ends
                RelatudeDBServer.Trace("The license pairing was answered; this installation now has a license.");
                return new PairingAnswer("ready", license.ToString("D"), apiKey.ToString("D"), null);
            }
            if (result.Status == "expired") _pairing = null;
            return new PairingAnswer(result.Status, null, null, null);
        } catch (Exception err) when (err is HttpRequestException or TaskCanceledException or JsonException) {
            // a poll that could not be made says nothing about the pairing, so it is not the end of it
            return new PairingAnswer("unreachable", null, null, err.Message);
        }
    }

    /// <summary>The admin UI gave up or the page was closed. Best effort: an abandoned pairing expires on its own.</summary>
    public async Task CancelPairingAsync(string pairingId, CancellationToken cancellationToken = default) {
        if (_pairing is not { } pairing || pairing.Handle.PairingId != pairingId) return;
        _pairing = null;
        try {
            var url = baseUrl + "/api/connect/pairings/" + Uri.EscapeDataString(pairing.Handle.PairingId) + "?secret=" + Uri.EscapeDataString(pairing.Secret);
            using var response = await http.DeleteAsync(url, cancellationToken);
        } catch (Exception err) when (err is HttpRequestException or TaskCanceledException) {
        }
    }

    // ------------------------------------------------------------------ the heartbeat

    /// <summary>
    /// Reports in shortly after start and every ten minutes after that, whenever the keys are set
    /// and <see cref="RelatudeDBServerSettings.DisableHeartbeat"/> is off. Failures are logged and
    /// nothing more.
    /// <para>One beat carries the API key, the installation key, the server and machine name, the
    /// build version and the total node count of the open databases. It carries no stored content,
    /// no queries and nothing about the people using the database. The answer says whether the
    /// license is valid and repeats the messages and stop switches the owner set on it; this server
    /// records that in <see cref="LastValidity"/> and acts on none of it.</para>
    /// <para>The timer is started either way and the switch is read at each beat, so an
    /// installation that disables reporting stops sending without a restart, and one that allows it
    /// again starts within the interval.</para>
    /// </summary>
    public void StartHeartbeat() {
        if (_heartbeat != null) return;
        _ = Task.Run(static () => fingerprint); // computed once; done here so no sign-in or heartbeat pays for it
        _heartbeat = new Timer(_ => _ = beatAsync(), null, _heartbeatFirst, _heartbeatInterval);
    }
    async Task beatAsync() {
        // read per beat rather than at start-up: switching it on in the settings page has to stop
        // the reporting there and then, not at the next restart
        if (settings.DisableHeartbeat) return;
        if (!tryGetApiKey(out var apiKey)) return;
        // A license server that is not running is the normal state of a developer's machine, and
        // finding that out through HttpClient costs a dozen first-chance exceptions every ten
        // minutes. Asking the socket first costs none. See TcpProbe for what a yes is worth: the
        // try below still handles everything this cannot rule out.
        if (!await TcpProbe.IsListeningAsync(baseUrl)) {
            RelatudeDBServer.Trace("License heartbeat skipped: nothing is listening at " + baseUrl + ".");
            return;
        }
        try {
            var nodes = 0L;
            foreach (var container in server.GetContainers()) {
                try { if (container.Store is { } store) nodes += store.Count(); } catch { } // a database that is not open has no count
            }
            var beat = new Heartbeat(apiKey, installationKey, settings.Name, (int)Math.Min(nodes, int.MaxValue),
                settings.Name, Environment.MachineName, typeof(LicenseLogin).Assembly.GetName().Version?.ToString());
            using var response = await http.PostAsJsonAsync(baseUrl + "/api/license/heartbeat", beat, _json);
            LastHeartbeatUtc = DateTime.UtcNow;
            if (!response.IsSuccessStatusCode) {
                RelatudeDBServer.Trace("The license server refused the heartbeat: " + await reasonOf(response));
                return;
            }
            var validity = await response.Content.ReadFromJsonAsync<Validity>(_json);
            if (validity == null) return;
            var changed = LastValidity == null || LastValidity.Valid != validity.Valid;
            LastValidity = validity;
            if (changed) RelatudeDBServer.Trace(validity.Valid ? "License heartbeat: the license is valid." : "License heartbeat: the license is not valid. " + validity.Reason);
        } catch (Exception err) {
            RelatudeDBServer.Trace("License heartbeat failed: " + err.Message);
        }
    }

    public void Dispose() {
        _heartbeat?.Dispose();
        _heartbeat = null;
    }
}
