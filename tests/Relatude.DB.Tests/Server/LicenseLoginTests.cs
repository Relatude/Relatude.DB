using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Relatude.Server;

/// <summary>
/// The API key is the one key an installation needs: the license server says which license it
/// belongs to, so the license key is found from it rather than trusted from the settings. These run
/// a real server against a stub license server on a loopback port, which answers for one API key.
/// </summary>
[TestClass]
public class LicenseLoginTests {

    static readonly Guid _apiKey = Guid.Parse("73235fe5-5e4e-4f4e-bd79-07ff5a2c236f");
    static readonly Guid _licenseId = Guid.Parse("6d85aa73-c5de-4cf7-9c01-902871f76b43");

    string _root = string.Empty;
    StubLicenseServer? _stub;

    [TestInitialize]
    public async Task Start() {
        _root = Path.Combine(Path.GetTempPath(), "relatude.license." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _stub = await StubLicenseServer.StartAsync(_apiKey, _licenseId);
    }

    [TestCleanup]
    public async Task Stop() {
        if (_stub != null) await _stub.DisposeAsync();
        try { Directory.Delete(_root, true); } catch { }
    }

    TestServerHost startServer(string? apiKey, string? licenseKey, string? servicesServerUrl = null, string? publicUrl = null) =>
        TestServerHost.Start(_root, configure: s => {
            s.ServicesServerUrl = servicesServerUrl ?? _stub!.Url;
            s.ApiKey = apiKey;
            s.LicenseKey = licenseKey;
            s.AllowLicenseeAdminLogin = true;
            s.DisableHeartbeat = true;
            s.PublicUrl = publicUrl;
        });

    /// <summary>A request as it reaches the server: the host name is whatever the sender put in it.</summary>
    static DefaultHttpContext request(string host, string query = "") {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    [TestMethod]
    public async Task LookUp_FindsTheLicenseKeyFromTheApiKey() {
        var host = startServer(apiKey: null, licenseKey: null);
        try {
            var found = await host.Server.LicenseLogin.LookUpAsync(_apiKey);
            Assert.AreEqual("valid", found.State);
            Assert.AreEqual(_licenseId, found.License!.Id);

            var unknown = await host.Server.LicenseLogin.LookUpAsync(Guid.NewGuid());
            Assert.AreEqual("invalid", unknown.State);
            Assert.AreEqual("Unknown API key.", unknown.Reason, "the license server's own words are passed on");
            Assert.IsNull(unknown.License);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Describe_WithOnlyTheApiKey_IsValidAndNamesTheLicenseKey() {
        var host = startServer(_apiKey.ToString(), licenseKey: null);
        try {
            Assert.IsTrue(host.Server.LicenseLogin.HasKeys, "the API key alone is enough");
            Assert.IsTrue(host.Server.LicenseLogin.SignInAvailable);
            var status = await host.Server.LicenseLogin.DescribeAsync();
            Assert.AreEqual("valid", status.State);
            Assert.AreEqual(_licenseId.ToString("D"), status.LicenseKey);
            Assert.IsFalse(status.HasLicenseKey, "nothing was written to the settings");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Describe_WithAContradictingLicenseKey_NamesTheApiKeysLicense() {
        var host = startServer(_apiKey.ToString(), "11111111-2222-3333-4444-555555555555");
        try {
            var status = await host.Server.LicenseLogin.DescribeAsync();
            Assert.AreEqual("valid", status.State);
            Assert.AreEqual(_licenseId.ToString("D"), status.LicenseKey, "the portal link has to name the license the API key belongs to");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Describe_WhenTheLicenseServerIsDown_FallsBackToTheSavedLicenseKey() {
        const string saved = "11111111-2222-3333-4444-555555555555";
        var host = startServer(_apiKey.ToString(), saved, servicesServerUrl: "http://127.0.0.1:1");
        try {
            var status = await host.Server.LicenseLogin.DescribeAsync();
            Assert.AreEqual("unreachable", status.State);
            Assert.AreEqual(saved, status.LicenseKey);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Describe_ShowsOnlyTheFirstFiveCharactersOfTheApiKey() {
        // saved in upper case with braces, it still starts the way the portal writes it
        var host = startServer(_apiKey.ToString("B").ToUpperInvariant(), licenseKey: null);
        try {
            var status = await host.Server.LicenseLogin.DescribeAsync();
            Assert.AreEqual("valid", status.State);
            Assert.AreEqual("73235", status.ApiKeyStart);

            host.Server.Settings.ApiKey = "short";
            Assert.IsNull((await host.Server.LicenseLogin.DescribeAsync()).ApiKeyStart, "a value too short to keep anything back shows nothing");
            host.Server.Settings.ApiKey = null;
            Assert.IsNull((await host.Server.LicenseLogin.DescribeAsync()).ApiKeyStart);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Describe_WithoutAUsableApiKey_DoesNotAskTheLicenseServer() {
        var host = startServer(apiKey: null, licenseKey: _licenseId.ToString());
        try {
            var missing = await host.Server.LicenseLogin.DescribeAsync();
            Assert.AreEqual("missing", missing.State);
            Assert.IsFalse(host.Server.LicenseLogin.HasKeys, "a license key alone is not enough");

            host.Server.Settings.ApiKey = "not a key";
            Assert.AreEqual("malformed", (await host.Server.LicenseLogin.DescribeAsync()).State);
            Assert.AreEqual(0, _stub!.LicenseLookups, "neither state should have asked the license server");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SignIn_PresentsTheApiKeysLicense_WhateverTheSettingsSay() {
        foreach (var saved in new string?[] { null, "11111111-2222-3333-4444-555555555555" }) {
            var host = startServer(_apiKey.ToString(), saved, publicUrl: "https://db.example.com");
            try {
                var context = request("db.example.com");
                await host.Server.LicenseLogin.StartAsync(context);

                Assert.AreEqual(StatusCodes.Status302Found, context.Response.StatusCode);
                StringAssert.StartsWith(context.Response.Headers.Location.ToString(), _stub!.Url + "/connect?request=r1", "saved license key: " + (saved ?? "none"));
                Assert.IsTrue(_stub.LoginRequests.TryDequeue(out var posted));
                Assert.AreEqual(_licenseId, posted.GetProperty("licenseKey").GetGuid());
                Assert.AreEqual(_apiKey, posted.GetProperty("apiKey").GetGuid());
            } finally {
                await host.DisposeAsync();
            }
        }
    }

    // ---- where the browser is sent back to with its code ----

    [TestMethod]
    public async Task SignIn_WithoutAPublicUrl_NeverReturnsToTheHostARequestNames() {
        var host = startServer(_apiKey.ToString(), licenseKey: null);
        try {
            // a server that answers on any name, reached by its IP address, say: the sender picks the host
            var context = request("evil.example");
            await host.Server.LicenseLogin.StartAsync(context);
            StringAssert.StartsWith(context.Response.Headers.Location.ToString(), host.Server.ApiUrlRoot + "/?login-error=", "refused, back to the login page");
            Assert.IsTrue(_stub!.LoginRequests.IsEmpty, "the license server is never asked to send a code there");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SignIn_OnALoopbackAddress_ReturnsToIt() {
        var host = startServer(_apiKey.ToString(), licenseKey: null);
        try {
            var context = request("localhost:5001");
            await host.Server.LicenseLogin.StartAsync(context);
            StringAssert.StartsWith(context.Response.Headers.Location.ToString(), _stub!.Url + "/connect?request=r1");
            Assert.IsTrue(_stub.LoginRequests.TryDequeue(out var posted));
            Assert.AreEqual("https://localhost:5001" + host.Server.ApiUrlRoot + "/auth/license-login/callback/", posted.GetProperty("redirectUri").GetString());
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SignIn_ReturnsToThePublicUrl_WhateverHostTheRequestNames() {
        var host = startServer(_apiKey.ToString(), licenseKey: null, publicUrl: "https://db.example.com/");
        try {
            // started on another name: sent to the public address first, so its state cookie is set where the browser comes back
            var elsewhere = request("evil.example", "?bg=%23102030");
            await host.Server.LicenseLogin.StartAsync(elsewhere);
            Assert.AreEqual("https://db.example.com" + host.Server.ApiUrlRoot + "/auth/license-login/start?bg=%23102030&on-public-url=1", elsewhere.Response.Headers.Location.ToString());
            Assert.IsTrue(_stub!.LoginRequests.IsEmpty);

            // sent on once only - and however it arrives then, the code goes to the public address
            var sentOn = request("evil.example", "?on-public-url=1");
            await host.Server.LicenseLogin.StartAsync(sentOn);
            StringAssert.StartsWith(sentOn.Response.Headers.Location.ToString(), _stub.Url + "/connect?request=r1");
            Assert.IsTrue(_stub.LoginRequests.TryDequeue(out var posted));
            Assert.AreEqual("https://db.example.com" + host.Server.ApiUrlRoot + "/auth/license-login/callback/", posted.GetProperty("redirectUri").GetString());
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SignIn_WithSeveralPublicUrls_ReturnsToTheOneItWasStartedOn() {
        // two sites sharing one settings file, one of them also on a port of its own
        var host = startServer(_apiKey.ToString(), licenseKey: null, publicUrl: "https://a.example.com/, https://b.example.com;\nhttps://b.example.com:8443");
        try {
            async Task<string> returnsTo(string requestHost) {
                var context = request(requestHost);
                await host.Server.LicenseLogin.StartAsync(context);
                StringAssert.StartsWith(context.Response.Headers.Location.ToString(), _stub!.Url + "/connect?request=r1", requestHost);
                Assert.IsTrue(_stub.LoginRequests.TryDequeue(out var posted), requestHost);
                return posted.GetProperty("redirectUri").GetString()!;
            }
            var callback = host.Server.ApiUrlRoot + "/auth/license-login/callback/";
            Assert.AreEqual("https://a.example.com" + callback, await returnsTo("a.example.com"));
            Assert.AreEqual("https://b.example.com" + callback, await returnsTo("B.example.com"), "host names compare ignoring case");
            Assert.AreEqual("https://b.example.com:8443" + callback, await returnsTo("b.example.com:8443"), "the port decides between addresses on one host name");
            Assert.AreEqual("https://b.example.com" + callback, await returnsTo("b.example.com:9999"), "and nothing more: a proxy may hand on another one");

            // a host that is not listed is sent to the first address, as with one
            var elsewhere = request("evil.example");
            await host.Server.LicenseLogin.StartAsync(elsewhere);
            Assert.AreEqual("https://a.example.com" + host.Server.ApiUrlRoot + "/auth/license-login/start?on-public-url=1", elsewhere.Response.Headers.Location.ToString());
            Assert.IsTrue(_stub!.LoginRequests.IsEmpty);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SignIn_RefusesAPublicUrlItCannotReturnTo() {
        foreach (var bad in new[] { "http://db.example.com", "db.example.com", "https://db.example.com/?x=1", "https://db.example.com, http://other.example.com" }) {
            var host = startServer(_apiKey.ToString(), licenseKey: null, publicUrl: bad);
            try {
                var context = request("db.example.com");
                await host.Server.LicenseLogin.StartAsync(context);
                StringAssert.StartsWith(context.Response.Headers.Location.ToString(), host.Server.ApiUrlRoot + "/?login-error=", bad);
                Assert.IsTrue(_stub!.LoginRequests.IsEmpty, bad);
            } finally {
                await host.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task RememberPublicUrl_TakesTheAddressTheServicesPageIsUsedOn_OnceAndNeverLoopback() {
        var host = startServer(_apiKey.ToString(), licenseKey: null);
        try {
            host.Server.LicenseLogin.RememberPublicUrl(request("localhost:5001"));
            Assert.IsNull(host.Server.Settings.PublicUrl, "a loopback address is right only on this machine");
            var plain = request("db.example.com");
            plain.Request.Scheme = "http";
            host.Server.LicenseLogin.RememberPublicUrl(plain);
            Assert.IsNull(host.Server.Settings.PublicUrl, "nor one the sign-in could not return to");

            host.Server.LicenseLogin.RememberPublicUrl(request("db.example.com"));
            Assert.AreEqual("https://db.example.com", host.Server.Settings.PublicUrl);
            host.Server.LicenseLogin.RememberPublicUrl(request("other.example.com"));
            Assert.AreEqual("https://db.example.com", host.Server.Settings.PublicUrl, "filled in once: after that it is changed in the settings, not by a request");
        } finally {
            await host.DisposeAsync();
        }
    }

    // ---- SMS senders ----

    [TestMethod]
    public async Task LookUp_ReadsTheApprovedSmsSenders() {
        var host = startServer(apiKey: null, licenseKey: null);
        try {
            var found = await host.Server.LicenseLogin.LookUpAsync(_apiKey);
            CollectionAssert.AreEqual(new[] { "Acme", "+4791234567" }, found.License!.SmsSenders);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task CheckSmsSender_PassesOnTheLicenseServersAnswer_AndRemembersOnlyAnAllowedOne() {
        var host = startServer(_apiKey.ToString(), licenseKey: null);
        try {
            var login = host.Server.LicenseLogin;
            Assert.IsNull(await login.CheckSmsSenderAsync("Acme"));
            Assert.IsNull(await login.CheckSmsSenderAsync("Acme"));
            Assert.AreEqual(1, _stub!.SenderChecks, "an allowed sender is not asked about again within the minute");
            Assert.AreEqual("Acme", _stub.LastSenderAsked);

            Assert.AreEqual("The sender Other is not approved for this license.", await login.CheckSmsSenderAsync("Other"), "the license server's own words are passed on");
            _stub.Approved.Add("Other");
            Assert.IsNull(await login.CheckSmsSenderAsync("Other"), "a refusal is never remembered, so a sender approved since is allowed at once");
            Assert.AreEqual(3, _stub.SenderChecks);

            Assert.IsNull(await login.CheckSmsSenderAsync("  "), "no sender is the service's own, and is not asked about");
            Assert.AreEqual(3, _stub.SenderChecks);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task CheckSmsSender_RefusesWhatItCannotCheck() {
        var host = startServer(apiKey: null, licenseKey: null);
        try {
            var noKey = await host.Server.LicenseLogin.CheckSmsSenderAsync("Acme");
            StringAssert.Contains(noKey, "no API key");
            Assert.AreEqual(0, _stub!.SenderChecks, "without a key there is nothing to ask");

            host.Server.Settings.ApiKey = Guid.NewGuid().ToString();
            Assert.AreEqual("Unknown API key.", await host.Server.LicenseLogin.CheckSmsSenderAsync("Acme"), "the key is answered first");

            host.Server.Settings.ApiKey = _apiKey.ToString();
            host.Server.Settings.ServicesServerUrl = "http://127.0.0.1:1";
            StringAssert.Contains(await host.Server.LicenseLogin.CheckSmsSenderAsync("Acme"), "could not be checked", "a license server that cannot be reached allows nothing");
        } finally {
            await host.DisposeAsync();
        }
    }

    /// <summary>The calls LicenseLogin makes here, answered for one API key the way Relatude.DB.Services answers them.</summary>
    sealed class StubLicenseServer : IAsyncDisposable {
        WebApplication _app = null!;
        int _licenseLookups;
        int _senderChecks;
        public string Url { get; private set; } = "";
        public int LicenseLookups => _licenseLookups;
        public int SenderChecks => _senderChecks;
        public string? LastSenderAsked { get; private set; }
        /// <summary>The senders approved for the license, compared ignoring case as the license server compares names.</summary>
        public ConcurrentBag<string> Approved { get; } = ["Acme", "+4791234567"];
        public ConcurrentQueue<JsonElement> LoginRequests { get; } = new();

        public static async Task<StubLicenseServer> StartAsync(Guid apiKey, Guid licenseId) {
            var stub = new StubLicenseServer();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.MapGet("/api/license", (HttpContext http) => {
                Interlocked.Increment(ref stub._licenseLookups);
                return Guid.TryParse(http.Request.Query["apiKey"], out var asked) && asked == apiKey
                    ? Results.Ok(new {
                        id = licenseId, name = "Stub license", disabled = false, expired = false, expiresUtc = (DateTime?)null,
                        features = Array.Empty<object>(), limits = Array.Empty<object>(), accounts = Array.Empty<object>(),
                        messageToAllEditors = "", messageToAllVisitors = "", stopEdit = false, stopVisit = false,
                        smsSenders = new[] { "Acme", "+4791234567" },
                    })
                    : Results.NotFound(new { reason = "Unknown API key." });
            });
            app.MapGet("/api/license/sms-sender", (HttpContext http) => {
                Interlocked.Increment(ref stub._senderChecks);
                var sender = http.Request.Query["sender"].ToString();
                stub.LastSenderAsked = sender;
                if (!Guid.TryParse(http.Request.Query["apiKey"], out var asked) || asked != apiKey) return Results.Ok(new { allowed = false, reason = "Unknown API key." });
                return stub.Approved.Any(s => string.Equals(s, sender, StringComparison.OrdinalIgnoreCase))
                    ? Results.Ok(new { allowed = true, reason = (string?)null })
                    : Results.Ok(new { allowed = false, reason = $"The sender {sender} is not approved for this license." });
            });
            app.MapPost("/api/connect/login-requests", async (HttpContext http) => {
                var body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body);
                stub.LoginRequests.Enqueue(body);
                return Results.Ok(new { requestId = "r1", loginUrl = stub.Url + "/connect?request=r1", expiresUtc = DateTime.UtcNow.AddMinutes(10) });
            });
            await app.StartAsync();
            stub._app = app;
            stub.Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
            return stub;
        }

        public async ValueTask DisposeAsync() {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
