using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Relatude.DB.NodeServer;

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

    TestServerHost startServer(string? apiKey, string? licenseKey, string? servicesServerUrl = null, string? publicUrl = null, Guid? id = null, string? root = null) =>
        TestServerHost.Start(root ?? _root, configure: s => {
            if (id is { } sharedId) s.Id = sharedId;
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
                var begun = await host.Server.LicenseLogin.BeginAsync(request("db.example.com"), new("https://db.example.com/relatude.db/", Bg: "#102030"));

                Assert.IsNull(begun.Error, begun.Error);
                Assert.AreEqual(_stub!.Url + "/connect?request=r1&bg=%23102030", begun.LoginUrl, "saved license key: " + (saved ?? "none"));
                Assert.IsTrue(_stub.LoginRequests.TryDequeue(out var posted));
                Assert.AreEqual(_licenseId, posted.GetProperty("licenseKey").GetGuid());
                Assert.AreEqual(_apiKey, posted.GetProperty("apiKey").GetGuid());
                Assert.AreEqual(43, posted.GetProperty("codeChallenge").GetString()!.Length, "an S256 challenge goes with every request");
            } finally {
                await host.DisposeAsync();
            }
        }
    }

    // ---- where the browser is sent back to with its code ----

    [TestMethod]
    public async Task SignIn_WithoutAPublicUrl_NeverReturnsToTheAddressARequestNames() {
        var host = startServer(_apiKey.ToString(), licenseKey: null);
        try {
            // a server that answers on any name, reached by its IP address, say: the sender picks the host and the page address
            var begun = await host.Server.LicenseLogin.BeginAsync(request("evil.example"), new("https://evil.example/relatude.db/"));
            Assert.IsNull(begun.LoginUrl);
            StringAssert.Contains(begun.Error, "PublicUrl");
            Assert.IsTrue(_stub!.LoginRequests.IsEmpty, "the license server is never asked to send a code there");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SignIn_OnALoopbackAddress_ReturnsToIt() {
        // whatever the public address: a code sent to a loopback address reaches only this machine
        foreach (var publicUrl in new[] { null, "https://db.example.com" }) {
            var host = startServer(_apiKey.ToString(), licenseKey: null, publicUrl: publicUrl);
            try {
                var begun = await host.Server.LicenseLogin.BeginAsync(request("localhost:5001"), new("https://localhost:5001/relatude.db/"));
                StringAssert.StartsWith(begun.LoginUrl, _stub!.Url + "/connect?request=r1", publicUrl);
                Assert.IsTrue(_stub.LoginRequests.TryDequeue(out var posted));
                Assert.AreEqual("https://localhost:5001" + host.Server.ApiUrlRoot + "/auth/license-login/callback/", posted.GetProperty("redirectUri").GetString());

                // a page that says it is on loopback, asked for over a public name, is not taken at its word
                var claimed = await host.Server.LicenseLogin.BeginAsync(request("db.example.com"), new("https://localhost:5001/relatude.db/"));
                Assert.IsNull(claimed.LoginUrl, publicUrl);
                Assert.IsTrue(_stub.LoginRequests.IsEmpty);
            } finally {
                await host.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task SignIn_ReturnsToTheAddressThePageIsOpenOn_NotTheHostNameTheRequestGives() {
        var host = startServer(_apiKey.ToString(), licenseKey: null, publicUrl: "https://db.example.com/");
        try {
            // behind a proxy that hands the server its own name: the page's address is the one the browser can come back to
            var begun = await host.Server.LicenseLogin.BeginAsync(request("10.0.0.5:8080"), new("https://db.example.com/relatude.db/?login-error=x"));
            StringAssert.StartsWith(begun.LoginUrl, _stub!.Url + "/connect?request=r1");
            Assert.IsTrue(_stub.LoginRequests.TryDequeue(out var posted));
            Assert.AreEqual("https://db.example.com" + host.Server.ApiUrlRoot + "/auth/license-login/callback/", posted.GetProperty("redirectUri").GetString());

            // a page on an address that is not listed is refused - never sent on to another site
            var elsewhere = await host.Server.LicenseLogin.BeginAsync(request("evil.example"), new("https://evil.example/relatude.db/"));
            Assert.IsNull(elsewhere.LoginUrl);
            StringAssert.Contains(elsewhere.Error, "https://evil.example is not one of this server's public addresses");
            StringAssert.Contains(elsewhere.Error, "Sign in on https://db.example.com instead");
            Assert.IsTrue(_stub.LoginRequests.IsEmpty);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SignIn_RefusesAPageAddressTheBrowserDidNotSendItFrom() {
        var host = startServer(_apiKey.ToString(), licenseKey: null, publicUrl: "https://db.example.com");
        try {
            var context = request("db.example.com");
            context.Request.Headers.Origin = "https://evil.example";
            var begun = await host.Server.LicenseLogin.BeginAsync(context, new("https://db.example.com/relatude.db/"));
            Assert.IsNull(begun.LoginUrl);
            StringAssert.Contains(begun.Error, "another address");

            context = request("db.example.com");
            context.Request.Headers.Origin = "https://db.example.com";
            Assert.IsNotNull((await host.Server.LicenseLogin.BeginAsync(context, new("https://db.example.com/relatude.db/"))).LoginUrl, "an Origin that agrees is fine");

            foreach (var page in new string?[] { null, "", "/relatude.db/", "ftp://db.example.com/" }) {
                Assert.IsNull((await host.Server.LicenseLogin.BeginAsync(request("db.example.com"), new(page))).LoginUrl, "page: " + page);
            }
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SignIn_WithSeveralPublicUrls_ReturnsToTheOneThePageIsOpenOn() {
        // two sites sharing one settings file, one of them also on a port of its own
        var host = startServer(_apiKey.ToString(), licenseKey: null, publicUrl: "https://a.example.com/, https://b.example.com;\nhttps://b.example.com:8443");
        try {
            async Task<string?> returnsTo(string page) {
                var begun = await host.Server.LicenseLogin.BeginAsync(request("internal:8080"), new(page));
                if (begun.LoginUrl == null) return null;
                Assert.IsTrue(_stub!.LoginRequests.TryDequeue(out var posted), page);
                return posted.GetProperty("redirectUri").GetString()!;
            }
            var callback = host.Server.ApiUrlRoot + "/auth/license-login/callback/";
            Assert.AreEqual("https://a.example.com" + callback, await returnsTo("https://a.example.com/relatude.db/"));
            Assert.AreEqual("https://b.example.com" + callback, await returnsTo("https://B.example.com/relatude.db/"), "host names compare ignoring case");
            Assert.AreEqual("https://b.example.com:8443" + callback, await returnsTo("https://b.example.com:8443/relatude.db/"));
            Assert.IsNull(await returnsTo("https://b.example.com:9999/relatude.db/"), "the browser says exactly where it is, so the port has to be listed too");
            Assert.IsNull(await returnsTo("http://a.example.com/relatude.db/"), "and the scheme");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SignIn_RefusesAPublicUrlItCannotReturnTo() {
        foreach (var bad in new[] { "http://db.example.com", "db.example.com", "https://db.example.com/?x=1", "https://db.example.com, http://other.example.com" }) {
            var host = startServer(_apiKey.ToString(), licenseKey: null, publicUrl: bad);
            try {
                var begun = await host.Server.LicenseLogin.BeginAsync(request("db.example.com"), new("https://db.example.com/relatude.db/"));
                Assert.IsNull(begun.LoginUrl, bad);
                StringAssert.Contains(begun.Error, "PublicUrl", bad);
                Assert.IsTrue(_stub!.LoginRequests.IsEmpty, bad);
            } finally {
                await host.DisposeAsync();
            }
        }
    }

    // ---- coming back with the code ----

    /// <summary>Starts a sign-in on a page at https://db.example.com and answers the ticket the browser was given in its cookie.</summary>
    async Task<string> beginOnDbExample(TestServerHost host) {
        var context = request("db.example.com");
        var begun = await host.Server.LicenseLogin.BeginAsync(context, new("https://db.example.com/relatude.db/"));
        Assert.IsNotNull(begun.LoginUrl, begun.Error);
        var cookie = context.Response.Headers.SetCookie.ToString();
        StringAssert.StartsWith(cookie, "RelatudeDBLicenseLogin=");
        StringAssert.Contains(cookie, "httponly");
        return cookie.Split(';')[0]["RelatudeDBLicenseLogin=".Length..];
    }

    /// <summary>The browser back at the callback with a code, carrying <paramref name="ticket"/> in its cookie; answers where it is sent next.</summary>
    static async Task<string> comeBack(TestServerHost host, string? ticket, string code = "c1", string state = "r1") {
        var context = request("db.example.com", "?code=" + code + "&state=" + state);
        if (ticket != null) context.Request.Headers.Cookie = "RelatudeDBLicenseLogin=" + ticket;
        await host.Server.LicenseLogin.CallbackAsync(context, code, state);
        return context.Response.Headers.Location.ToString();
    }

    [TestMethod]
    public async Task Callback_TradesTheCodeWithTheSecretBehindTheChallenge_Once() {
        var host = startServer(_apiKey.ToString(), licenseKey: null, publicUrl: "https://db.example.com");
        try {
            var ticket = await beginOnDbExample(host);
            Assert.IsTrue(_stub!.LoginRequests.TryDequeue(out var started));
            Assert.AreNotEqual("r1", ticket, "the cookie holds a ticket of its own, not the request id every url shows");

            Assert.AreEqual(host.Server.ApiUrlRoot + "/", await comeBack(host, ticket), "signed in");
            Assert.IsTrue(_stub.TokenRequests.TryDequeue(out var traded));
            Assert.AreEqual("r1", traded.GetProperty("state").GetString());
            var verifier = traded.GetProperty("codeVerifier").GetString()!;
            Assert.AreEqual(started.GetProperty("codeChallenge").GetString(), Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
                "the code is traded with the secret whose hash went with the request");

            StringAssert.Contains(await comeBack(host, ticket), "login-error=", "a ticket is good once");
            Assert.IsTrue(_stub.TokenRequests.IsEmpty);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Callback_RefusesASignInThisServerDidNotStart() {
        var host = startServer(_apiKey.ToString(), licenseKey: null, publicUrl: "https://db.example.com");
        try {
            var ticket = await beginOnDbExample(host);
            StringAssert.Contains(await comeBack(host, ticket: null), "login-error=", "no cookie");
            StringAssert.Contains(await comeBack(host, "r1"), "login-error=", "the request id from the url is no ticket");
            StringAssert.Contains(await comeBack(host, Guid.NewGuid().ToString("N")), "login-error=", "nor is anything made up");

            ticket = await beginOnDbExample(host);
            StringAssert.Contains(await comeBack(host, ticket, state: "r2"), "login-error=", "the code has to come back for the request the ticket started");
            StringAssert.Contains(await comeBack(host, ticket), "login-error=", "and that spent the ticket");
            Assert.IsTrue(_stub!.TokenRequests.IsEmpty, "the license server was never asked");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Callback_RefusesASignInStartedOnAnotherServerWithTheSameKeys() {
        // two sites from one settings file, as on two App Services: the same API key and the same installation key
        var sharedId = Guid.NewGuid();
        var first = startServer(_apiKey.ToString(), licenseKey: null, publicUrl: "https://db.example.com", id: sharedId);
        var secondRoot = Path.Combine(_root, "second");
        Directory.CreateDirectory(secondRoot);
        var second = startServer(_apiKey.ToString(), licenseKey: null, publicUrl: "https://db.example.com", id: sharedId, root: secondRoot);
        try {
            var ticket = await beginOnDbExample(first);
            StringAssert.Contains(await comeBack(second, ticket), "login-error=", "the ticket lives in the memory of the server that started the sign-in");
            Assert.IsTrue(_stub!.TokenRequests.IsEmpty);
            Assert.AreEqual(first.Server.ApiUrlRoot + "/", await comeBack(first, ticket), "where it still completes");
        } finally {
            await second.DisposeAsync();
            await first.DisposeAsync();
        }
    }

    // ---- addresses approved in Relatude Services ----

    [TestMethod]
    public async Task SignIn_OnAnAddressApprovedInRelatudeServices_ReturnsToIt() {
        var host = startServer(_apiKey.ToString(), licenseKey: null);
        try {
            _stub!.ApprovedAddresses = ["portal.example.com", "portal.example.com:8443"];
            var begun = await host.Server.LicenseLogin.BeginAsync(request("internal:8080"), new("https://Portal.example.com/relatude.db/"));
            Assert.IsNull(begun.Error, begun.Error);
            Assert.IsTrue(_stub.LoginRequests.TryDequeue(out var posted));
            Assert.AreEqual("https://portal.example.com" + host.Server.ApiUrlRoot + "/auth/license-login/callback/", posted.GetProperty("redirectUri").GetString());
            Assert.IsTrue(_stub.AddressQuestions.TryDequeue(out var asked));
            Assert.AreEqual(_apiKey, asked.GetProperty("apiKey").GetGuid());
            Assert.AreEqual(posted.GetProperty("installationKey").GetString(), asked.GetProperty("installationKey").GetString(), "asked about this installation");
            Assert.AreEqual(host.Server.LicenseLogin.DescribeInstallation().Key, posted.GetProperty("installationKey").GetString(), "the key the Services page shows is the one sent");

            Assert.IsNotNull((await host.Server.LicenseLogin.BeginAsync(request("internal:8080"), new("https://portal.example.com:8443/relatude.db/"))).LoginUrl);
            Assert.IsTrue(_stub.AddressQuestions.IsEmpty, "an approved address is not asked about again straight away");
            Assert.IsTrue(_stub.LoginRequests.TryDequeue(out posted));
            Assert.AreEqual("https://portal.example.com:8443" + host.Server.ApiUrlRoot + "/auth/license-login/callback/", posted.GetProperty("redirectUri").GetString());
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SignIn_OnAnAddressNeitherListedNorApproved_IsRefusedWithoutALoginRequest() {
        var host = startServer(_apiKey.ToString(), licenseKey: null, publicUrl: "https://db.example.com");
        try {
            _stub!.ApprovedAddresses = ["portal.example.com"];
            foreach (var page in new[] { "https://evil.example/relatude.db/", "https://portal.example.com:9999/relatude.db/", "https://sub.portal.example.com/relatude.db/" }) {
                var begun = await host.Server.LicenseLogin.BeginAsync(request("evil.example"), new(page));
                Assert.IsNull(begun.LoginUrl, page);
                StringAssert.Contains(begun.Error, "is not one of this server's public addresses", page);
                StringAssert.Contains(begun.Error, "Relatude Services", page);
            }
            Assert.IsTrue(_stub.LoginRequests.IsEmpty, "the license server is never asked to send a code there, nor to put it up for approval");

            // http is never asked about: the license server would not send a browser back there
            while (_stub.AddressQuestions.TryDequeue(out _)) { }
            var plain = await host.Server.LicenseLogin.BeginAsync(request("portal.example.com"), new("http://portal.example.com/relatude.db/"));
            StringAssert.Contains(plain.Error, "https");
            Assert.IsTrue(_stub.AddressQuestions.IsEmpty);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task SignIn_WithALicenseServerThatDoesNotKnowTheQuestion_TakesOnlyItsOwnAddresses() {
        var host = startServer(_apiKey.ToString(), licenseKey: null);
        try {
            _stub!.ApprovedAddresses = null; // answers 404, as a license server from before it does
            var begun = await host.Server.LicenseLogin.BeginAsync(request("db.example.com"), new("https://db.example.com/relatude.db/"));
            Assert.IsNull(begun.LoginUrl);
            StringAssert.Contains(begun.Error, "PublicUrl");
            Assert.IsTrue(_stub.LoginRequests.IsEmpty);
        } finally {
            await host.DisposeAsync();
        }
    }

    // ---- the public addresses filling themselves in ----

    [TestMethod]
    public async Task RememberPublicUrl_AddsEachHttpsAddressOnce_AndNeverLoopback() {
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
            host.Server.LicenseLogin.RememberPublicUrl(request("DB.example.com"));
            Assert.AreEqual("https://db.example.com", host.Server.Settings.PublicUrl, "the same address, however it is written, once");
            host.Server.LicenseLogin.RememberPublicUrl(request("other.example.com:8443"));
            Assert.AreEqual("https://db.example.com, https://other.example.com:8443", host.Server.Settings.PublicUrl, "another address the admin UI is used on is added");

            var begun = await host.Server.LicenseLogin.BeginAsync(request("other.example.com:8443"), new("https://other.example.com:8443/relatude.db/"));
            Assert.IsNotNull(begun.LoginUrl, begun.Error);
            Assert.IsTrue(_stub!.AddressQuestions.IsEmpty, "a listed address is this server's own; Relatude Services is not asked");
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RememberPublicUrl_LeavesAValueTheConfigurationDecides() {
        var host = TestServerHost.Start(_root, configure: s => {
            s.ServicesServerUrl = _stub!.Url;
            s.ApiKey = _apiKey.ToString();
            s.DisableHeartbeat = true;
        }, configuration: new() { ["RelatudeDB:PublicUrl"] = "https://configured.example.com" });
        try {
            host.Server.LicenseLogin.RememberPublicUrl(request("db.example.com"));
            Assert.AreEqual("https://configured.example.com", host.Server.Settings.PublicUrl);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task OpeningTheAdminUI_SignedIn_AddsItsAddress_TheLocalhostBypassDoesNot() {
        var host = startServer(_apiKey.ToString(), licenseKey: null);
        try {
            host.Server.Settings.MasterUserName = "admin";
            host.Server.Settings.MasterPassword = "secret";
            host.Server.Settings.AllowMasterLoginOutsideLocalhost = true;
            typeof(RelatudeDBServer).GetMethod("MapAdminAPI", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Server, [host.App]);

            await whoami(host, request("db.example.com"));
            Assert.IsNull(host.Server.Settings.PublicUrl, "no session: nobody vouches for the address");

            var login = request("db.example.com");
            host.Server.Authentication.LogIn(login, remember: false);
            var session = login.Response.Headers.SetCookie.ToString().Split(';')[0];
            var signedIn = request("db.example.com");
            signedIn.Request.Headers.Cookie = session;
            await whoami(host, signedIn);
            Assert.AreEqual("https://db.example.com", host.Server.Settings.PublicUrl);
        } finally {
            await host.DisposeAsync();
        }
    }

    static async Task whoami(TestServerHost host, DefaultHttpContext http) {
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"type":"whoami","payload":{}}"""));
        var result = await host.Server.UI!.Commands.Execute(http);
        Assert.AreEqual(200, ((IStatusCodeHttpResult)result).StatusCode ?? 200);
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
        public ConcurrentQueue<JsonElement> TokenRequests { get; } = new();
        public ConcurrentQueue<JsonElement> AddressQuestions { get; } = new();
        /// <summary>The addresses approved for the installation in the portal; null answers 404, as a license server from before the question does.</summary>
        public string[]? ApprovedAddresses { get; set; } = [];
        string _lastInstallationKey = "";

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
                stub._lastInstallationKey = body.GetProperty("installationKey").GetString()!;
                return Results.Ok(new { requestId = "r1", loginUrl = stub.Url + "/connect?request=r1", expiresUtc = DateTime.UtcNow.AddMinutes(10) });
            });
            app.MapPost("/api/connect/approved-addresses", async (HttpContext http) => {
                var body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body);
                stub.AddressQuestions.Enqueue(body);
                return stub.ApprovedAddresses is { } addresses ? Results.Ok(new { addresses }) : Results.NotFound();
            });
            // takes any code, and leaves checking what came with it to the test
            app.MapPost("/api/connect/token", async (HttpContext http) => {
                var body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body);
                stub.TokenRequests.Enqueue(body);
                return Results.Ok(new {
                    subject = Guid.NewGuid(), name = "Kari Nordmann", email = "", mobile = "", licenseId, licenseName = "Stub license",
                    installationId = Guid.NewGuid(), installationKey = stub._lastInstallationKey, role = "owner",
                    issuedUtc = DateTime.UtcNow, expiresUtc = DateTime.UtcNow.AddHours(1),
                });
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
