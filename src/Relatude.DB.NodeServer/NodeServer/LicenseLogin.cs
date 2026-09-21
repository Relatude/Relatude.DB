using System.Net.Http.Json;
using System.Text.Json;
using Relatude.DB.NodeServer.Settings;
namespace Relatude.DB.NodeServer;
/// <summary>
/// This installation's side of Relatude.License: letting an admin sign in with a Relatude.License
/// account, and reporting in (the heartbeat). Both need the license key and an API key from the
/// portal in the settings; the sign-in also needs AllowLicenseeAdminLogin.
/// <para>The sign-in is a redirect in three steps. First this server tells the license server, over
/// the back channel with its API key, that a browser is about to come and where to send it back;
/// the license server answers with a sign-in url, and the browser is sent there with nothing but a
/// request id (the redirect uri never travels through the browser). The user signs in there and
/// the license server checks that they own the license or have been granted this installation.
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

    /// <summary>Both keys are set and are guids; without them there is nothing to say to the license server.</summary>
    public bool HasKeys => tryGetKeys(out _, out _);
    /// <summary>The login page may offer "Sign in with Relatude.License".</summary>
    public bool SignInAvailable => settings.AllowLicenseeAdminLogin && HasKeys;

    bool tryGetKeys(out Guid licenseKey, out Guid apiKey) {
        apiKey = default;
        return Guid.TryParse(settings.LicenseKey, out licenseKey) && Guid.TryParse(settings.ApiKey, out apiKey);
    }
    string baseUrl => (string.IsNullOrWhiteSpace(settings.LicenseServerUrl) ? Defaults.LicenseServerUrl : settings.LicenseServerUrl).TrimEnd('/');
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
        if (!SignInAvailable || !tryGetKeys(out var licenseKey, out var apiKey)) { failed(context, "Sign-in with Relatude.License is not enabled on this server."); return; }
        var redirectUri = $"{context.Request.Scheme}://{context.Request.Host}{server.ApiUrlPublic}license-login/callback/";
        var request = new LoginRequestCreate(apiKey, licenseKey, installationKey, redirectUri, settings.Name);
        try {
            using var response = await http.PostAsJsonAsync(baseUrl + "/api/connect/login-requests", request, _json, context.RequestAborted);
            if (!response.IsSuccessStatusCode) { failed(context, "The license server refused the sign-in: " + await reasonOf(response)); return; }
            var created = await response.Content.ReadFromJsonAsync<LoginRequestCreated>(_json, context.RequestAborted);
            if (created == null || string.IsNullOrEmpty(created.LoginUrl) || string.IsNullOrEmpty(created.RequestId)) { failed(context, "The license server answered without a sign-in url."); return; }
            // binds the browser that leaves to the one that comes back with the code
            context.Response.Cookies.Append(_stateCookie, created.RequestId, stateCookieOptions(_stateLifetime));
            context.Response.Redirect(created.LoginUrl);
        } catch (Exception err) when (err is HttpRequestException or TaskCanceledException or JsonException) {
            RelatudeDBServer.Trace("Sign-in with Relatude.License could not start: " + err.Message);
            failed(context, "The license server could not be reached. Use the master login, or try again later.");
        }
    }

    /// <summary>Step three: the browser is back with a code; trade it for the user and open the session.</summary>
    public async Task CallbackAsync(HttpContext context, string? code, string? state) {
        var expected = context.Request.Cookies[_stateCookie];
        context.Response.Cookies.Delete(_stateCookie, stateCookieOptions(null));
        if (!SignInAvailable || !tryGetKeys(out _, out var apiKey)) { failed(context, "Sign-in with Relatude.License is not enabled on this server."); return; }
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || expected != state) { failed(context, "The sign-in did not come back the way it left. Try again."); return; }
        try {
            using var response = await http.PostAsJsonAsync(baseUrl + "/api/connect/token", new TokenRequest(apiKey, code, state), _json, context.RequestAborted);
            if (!response.IsSuccessStatusCode) { failed(context, "The license server refused the sign-in: " + await reasonOf(response)); return; }
            var token = await response.Content.ReadFromJsonAsync<TokenResult>(_json, context.RequestAborted);
            if (token == null || token.Subject == Guid.Empty) { failed(context, "The license server answered without a user."); return; }
            if (!string.Equals(token.InstallationKey, installationKey, StringComparison.OrdinalIgnoreCase)) { failed(context, "The sign-in was meant for another installation."); return; }
            server.Authentication.LogInLicensee(context, token.Subject.ToString(), token.Name, token.ExpiresUtc);
            RelatudeDBServer.Trace($"{token.Name} signed in with Relatude.License as {token.Role} of the license \"{token.LicenseName}\".");
            context.Response.Redirect(server.ApiUrlRoot + "/");
        } catch (Exception err) when (err is HttpRequestException or TaskCanceledException or JsonException) {
            RelatudeDBServer.Trace("Sign-in with Relatude.License could not complete: " + err.Message);
            failed(context, "The license server could not be reached. Use the master login, or try again later.");
        }
    }

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

    // ------------------------------------------------------------------ the heartbeat

    /// <summary>Reports in shortly after start and every ten minutes after that, whenever the keys are set. Failures are logged and nothing more.</summary>
    public void StartHeartbeat() {
        if (_heartbeat != null) return;
        _ = Task.Run(static () => fingerprint); // computed once; done here so no sign-in or heartbeat pays for it
        _heartbeat = new Timer(_ => _ = beatAsync(), null, _heartbeatFirst, _heartbeatInterval);
    }
    async Task beatAsync() {
        if (!tryGetKeys(out _, out var apiKey)) return;
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
