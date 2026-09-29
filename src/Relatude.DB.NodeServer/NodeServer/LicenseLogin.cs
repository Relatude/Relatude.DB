using System.Net.Http.Json;
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
/// <para>The sign-in is a redirect in three steps. First this server tells the license server, over
/// the back channel with its API key, that a browser is about to come and where to send it back;
/// the license server answers with a sign-in url, and the browser is sent there with nothing but a
/// request id (the redirect uri never travels through the browser). Where to send it back is this
/// server's public address, never the host name a request gives (see <see cref="returnAddress"/>).
/// The user signs in there and the license server checks that they have access to the license.
/// The browser then comes back to the callback with a one-time code, which this server trades,
/// again over the back channel with its API key, for who the user is - and opens its own session.
/// A code is useless without the API key, and the master login keeps working throughout.</para>
/// </summary>
public sealed class LicenseLogin(RelatudeDBServer server) : IDisposable {
    // The shapes the license server speaks (Relatude.DB.Services: Connect/ConnectContracts.cs and
    // Licensing/LicensingContracts.cs). Only the fields used here; change both sides together.
    sealed record LoginRequestCreate(Guid ApiKey, Guid LicenseKey, string InstallationKey, string RedirectUri, string? InstallationName);
    sealed record LoginRequestCreated(string RequestId, string LoginUrl, DateTime ExpiresUtc);
    sealed record TokenRequest(Guid ApiKey, string Code, string State); // State: the request id, so the code is redeemable only for the sign-in it came from
    sealed record TokenResult(Guid Subject, string Name, string Email, string Mobile, Guid LicenseId, string LicenseName, Guid InstallationId, string InstallationKey, string Role, DateTime IssuedUtc, DateTime ExpiresUtc);
    sealed record Heartbeat(Guid ApiKey, string InstallationKey, string? Name, int Nodes, string? ServerName, string? MachineName, string? BuildVersion);
    sealed record Refusal(string? Reason);
    /// <summary>The license server's answer to a heartbeat: may this installation keep running, and the soft switches of its license.</summary>
    public sealed record Validity(bool Valid, string? Reason, string MessageToAllEditors = "", string MessageToAllVisitors = "", bool StopEdit = false, bool StopVisit = false);

    /// <summary>What the license carries, as the license server tells an installation holding its API key.</summary>
    public sealed record LicenseInfo(
        Guid Id, string Name, bool Disabled, bool Expired, DateTime? ExpiresUtc,
        string[] Features, LimitInfo[] Limits, AccountInfo[] Accounts,
        string MessageToAllEditors, string MessageToAllVisitors, bool StopEdit, bool StopVisit) {
        /// <summary>Neither disabled nor expired: its features, limits and credits are honoured.</summary>
        public bool Active => !Disabled && !Expired;
    }
    /// <summary>A numeric cap on the license.</summary>
    public sealed record LimitInfo(string Name, int MaxValue, bool Unlimited);
    /// <summary>A monthly credit account and its rate limits.</summary>
    public sealed record AccountInfo(string Name, int MonthlyLimit, int UsedThisMonth, int BalanceLeft, RateWindow Minute, RateWindow Hour, RateWindow Day);
    /// <summary>Credits allowed and spent in one clock window. A limit of zero means no limit.</summary>
    public sealed record RateWindow(int Limit, int Used);

    /// <summary>
    /// How this installation stands with the license server, for the Services page. One of:
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
        PairingHandle? Pairing);

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
    /// What this installation calls itself towards the license server: the persisted server id and the
    /// machine fingerprint, joined. The id alone travels with relatude.db.json, so a settings file copied
    /// to five servers would look like one installation; the fingerprint alone is shared by every
    /// installation on one machine and changes when the hardware does. Together they count what the
    /// license server wants counted: a settings file running on N machines shows as N installations
    /// with the same id, N sites on one machine as N installations with the same fingerprint.
    /// </summary>
    string installationKey => settings.Id.ToString() + ":" + fingerprint;
    /// <summary>
    /// InstallationIdentity.Get() is deterministic and cached after the first call, but that first call
    /// reads hardware identifiers and may spawn a process (up to three seconds on Windows), so
    /// <see cref="StartHeartbeat"/> warms it up off the request path.
    /// </summary>
    static string fingerprint => Relatude.DB.Common.InstallationIdentity.Get();

    /// <summary>What the login page asks before it decides whether to show the button.</summary>
    public object DescribeOptions() => new { Available = SignInAvailable, ServerUrl = SignInAvailable ? baseUrl : null };

    // Both handlers write their redirect straight to the response rather than returning an IResult.
    // A lambda of the shape (HttpContext) => Task<IResult> is also a RequestDelegate, and MapGet
    // prefers that overload: the task is awaited and its result silently dropped, so the browser
    // would get an empty 200 instead of the redirect. Writing the response directly is correct
    // whichever overload the mapper ends up with.

    /// <summary>Step one: register the sign-in with the license server and send the browser there.</summary>
    public async Task StartAsync(HttpContext context) {
        if (!SignInAvailable || !tryGetApiKey(out var apiKey)) { failed(context, "Sign-in with Relatude Services is not enabled on this server."); return; }
        var returnTo = returnAddress(context);
        if (returnTo.Refusal is { } refusal) { failed(context, refusal); return; }
        if (returnTo.StartAt is { } startAt) { context.Response.Redirect(startAt); return; }
        var redirectUri = returnTo.Base + server.ApiUrlPublic + "license-login/callback/";
        // the same question the heartbeat asks, and here it also answers the user faster than
        // waiting out the http timeout would
        if (!await TcpProbe.IsListeningAsync(baseUrl, cancellationToken: context.RequestAborted)) { unreachable(context, "start"); return; }
        // The license key goes with the API key, and the license server only takes the one the API
        // key belongs to - so it is that one, found from the API key the first time it is needed.
        if (_known is not { } known || known.ApiKey != apiKey) {
            var lookup = await LookUpAsync(apiKey, context.RequestAborted);
            if (lookup.License == null) {
                RelatudeDBServer.Trace("Sign-in with Relatude.License could not start: the API key was not looked up. " + lookup.Reason);
                failed(context, lookup.State == "invalid"
                    ? "Relatude Services does not accept this server's API key: " + lookup.Reason
                    : "Relatude Services could not be reached. Use the master login, or try again later.");
                return;
            }
            known = new KnownLicense(apiKey, lookup.License.Id);
        }
        var request = new LoginRequestCreate(apiKey, known.LicenseKey, installationKey, redirectUri, settings.Name);
        try {
            using var response = await http.PostAsJsonAsync(baseUrl + "/api/connect/login-requests", request, _json, context.RequestAborted);
            if (!response.IsSuccessStatusCode) { failed(context, "Relatude Services refused the sign-in: " + await reasonOf(response)); return; }
            var created = await response.Content.ReadFromJsonAsync<LoginRequestCreated>(_json, context.RequestAborted);
            if (created == null || string.IsNullOrEmpty(created.LoginUrl) || string.IsNullOrEmpty(created.RequestId)) { failed(context, "Relatude Services answered without a sign-in url."); return; }
            // binds the browser that leaves to the one that comes back with the code
            context.Response.Cookies.Append(_stateCookie, created.RequestId, stateCookieOptions(_stateLifetime));
            context.Response.Redirect(withQuery(created.LoginUrl, colourParameters(context.Request)));
        } catch (Exception err) when (err is HttpRequestException or TaskCanceledException or JsonException) {
            RelatudeDBServer.Trace("Sign-in with Relatude.License could not start: " + err.Message);
            failed(context, "Relatude Services could not be reached. Use the master login, or try again later.");
        }
    }

    /// <summary>Step three: the browser is back with a code; trade it for the user and open the session.</summary>
    public async Task CallbackAsync(HttpContext context, string? code, string? state) {
        var expected = context.Request.Cookies[_stateCookie];
        context.Response.Cookies.Delete(_stateCookie, stateCookieOptions(null));
        if (!SignInAvailable || !tryGetApiKey(out var apiKey)) { failed(context, "Sign-in with Relatude Services is not enabled on this server."); return; }
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || expected != state) { failed(context, "The sign-in did not come back the way it left. Try again."); return; }
        if (!await TcpProbe.IsListeningAsync(baseUrl, cancellationToken: context.RequestAborted)) { unreachable(context, "complete"); return; }
        try {
            using var response = await http.PostAsJsonAsync(baseUrl + "/api/connect/token", new TokenRequest(apiKey, code, state), _json, context.RequestAborted);
            if (!response.IsSuccessStatusCode) { failed(context, "Relatude Services refused the sign-in: " + await reasonOf(response)); return; }
            var token = await response.Content.ReadFromJsonAsync<TokenResult>(_json, context.RequestAborted);
            if (token == null || token.Subject == Guid.Empty) { failed(context, "Relatude Services answered without a user."); return; }
            if (!string.Equals(token.InstallationKey, installationKey, StringComparison.OrdinalIgnoreCase)) { failed(context, "The sign-in was meant for another installation."); return; }
            server.Authentication.LogInLicensee(context, token.Subject.ToString(), token.Name, token.ExpiresUtc);
            RelatudeDBServer.Trace($"{token.Name} signed in with Relatude.License as {token.Role} of the license \"{token.LicenseName}\".");
            context.Response.Redirect(server.ApiUrlRoot + "/");
        } catch (Exception err) when (err is HttpRequestException or TaskCanceledException or JsonException) {
            RelatudeDBServer.Trace("Sign-in with Relatude.License could not complete: " + err.Message);
            failed(context, "Relatude Services could not be reached. Use the master login, or try again later.");
        }
    }

    /// <summary>
    /// Where the browser comes back to, as <see cref="returnAddress"/> decides it: the base to build
    /// the callback url on, or where to start the sign-in instead, or why it cannot start. One is set.
    /// </summary>
    sealed record ReturnAddress(string? Base, string? StartAt, string? Refusal);

    /// <summary>A sign-in sent on to the public address carries this, so it is sent on once only.</summary>
    const string _onPublicUrl = "on-public-url";

    /// <summary>
    /// Where the license server sends the browser back to with the code: <see cref="RelatudeDBServerSettings.PublicUrl"/>
    /// when it is set, and otherwise this request's own address only when that is a loopback one.
    /// <para>A request's host name is not enough on its own, because whoever sends the request chooses
    /// it. A server that answers on any name - reached by its IP address, say - would register a
    /// sign-in that returns to an address of the sender's choosing. Should someone with access to the
    /// license then approve that address on the sign-in page, their code would go to the sender, who
    /// could bring it here with the state cookie of the sign-in they started, and be signed in as
    /// them. A code sent to a loopback address reaches nobody but the machine it is on.</para>
    /// <para>A sign-in started on another host than the public address is sent there first, so that
    /// its state cookie is set where the browser comes back to - once only: behind a proxy that hands
    /// this server another host name than the browser used, it would otherwise go round for ever.</para>
    /// </summary>
    ReturnAddress returnAddress(HttpContext context) {
        if (!string.IsNullOrWhiteSpace(settings.PublicUrl)) {
            if (!tryPublicBase(settings.PublicUrl, out var publicUri, out var publicBase))
                return new(null, null, "The public address in the settings (PublicUrl) is not one the sign-in can return to: it has to be an https address such as https://db.example.com. Fix it in the settings, or use the master login.");
            if (!string.Equals(context.Request.Host.Host, publicUri.Host, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(context.Request.Query[_onPublicUrl])) {
                var query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value![1..] + "&" : "";
                return new(null, withQuery(publicBase + server.ApiUrlPublic + "license-login/start", query + _onPublicUrl + "=1"), null);
            }
            return new(publicBase, null, null);
        }
        if (isLoopback(context.Request)) return new($"{context.Request.Scheme}://{context.Request.Host}", null, null);
        return new(null, null, "Sign-in with Relatude Services is not set up for this address yet: the server's public address (PublicUrl) is not set. "
            + "It is filled in when the API key is saved or the installation is paired on the Services page, or it can be set in the settings. Until then, use the master login.");
    }

    /// <summary>
    /// Fills in <see cref="RelatudeDBServerSettings.PublicUrl"/>, when it is not set yet, from the
    /// address the Services page is being used on. It is asked as the API key is saved or the
    /// installation paired, by someone signed in to this admin UI - the one kind of request whose host
    /// name can be taken at its word. A loopback address is not remembered: it is right only on this
    /// machine, where the sign-in needs no public address. Nor is one that is not https, and a value
    /// that configuration decides is left alone.
    /// </summary>
    public void RememberPublicUrl(HttpContext context) {
        lock (_rememberLock) {
            if (!string.IsNullOrWhiteSpace(settings.PublicUrl)) return;
            var overlay = server.ConfigurationOverlay;
            if (overlay != null && overlay.IsOverridden(SettingsOverlay.OverridePath(null, nameof(RelatudeDBServerSettings.PublicUrl)), out _)) return;
            var seen = context.Request.Scheme + "://" + context.Request.Host.Value + context.Request.PathBase.Value;
            if (!tryPublicBase(seen, out var uri, out var publicBase) || uri.IsLoopback) return;
            settings.PublicUrl = publicBase;
            server.UpdateWAFServerSettingsFile();
            RelatudeDBServer.Trace("Sign-in with Relatude.License will send browsers back to " + publicBase + ", the address the Services page was used on. "
                + "Change PublicUrl in the settings if that is not this server's public address.");
        }
    }
    readonly object _rememberLock = new();

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
    void unreachable(HttpContext context, string step) {
        RelatudeDBServer.Trace($"Sign-in with Relatude.License could not {step}: nothing is listening at {baseUrl}.");
        failed(context, "Relatude Services could not be reached. Use the master login, or try again later.");
    }

    /// <summary>
    /// The colours of the login screen the sign-in was started from (Login.tsx sends them), passed on
    /// to the license server's landing page. When the user can go straight back, that page shows
    /// nothing but a progress line - in these colours it reads as the same screen, not a stop at
    /// another site in between. Only plain hex colours are passed on; anything else is dropped.
    /// </summary>
    static string colourParameters(HttpRequest request) {
        var parts = new List<string>();
        foreach (var name in _colourParameters) {
            var value = request.Query[name].ToString();
            if (isHexColour(value)) parts.Add(name + "=" + Uri.EscapeDataString(value));
        }
        return string.Join("&", parts);
    }
    static readonly string[] _colourParameters = ["bg", "track", "bar"];
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

    // ------------------------------------------------------------------ what the Services page shows

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
        // an answer for the API key decides the license key, whatever the settings say
        LicenseStatus state(string name, string? reason, LicenseInfo? license = null) => new(
            name, reason, baseUrl, savedLicenseKey != null, hasApiKey, apiKeyStart, license?.Id.ToString("D") ?? savedLicenseKey,
            settings.AllowLicenseeAdminLogin, settings.DisableHeartbeat, LastHeartbeatUtc, license, PendingPairing);

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
    /// Any API key can be asked about, not only the one in the settings - the Services page checks a
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
