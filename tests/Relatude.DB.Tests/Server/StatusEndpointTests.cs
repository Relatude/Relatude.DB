using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Relatude.DB.NodeServer.API;

namespace Relatude.Server;

/// <summary>
/// /status.relatude.db is answered by the startup middleware in every state the server can be in,
/// before anything else sees the request, so these drive that middleware directly.
/// </summary>
[TestClass]
public class StatusEndpointTests {

    string _root = string.Empty;

    [TestInitialize]
    public void CreateRoot() {
        _root = Path.Combine(Path.GetTempPath(), "relatude.status." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void DeleteRoot() {
        try { Directory.Delete(_root, true); } catch { }
    }

    [TestMethod]
    public async Task OpenDatabase_Answers200WithStateAndVersion() {
        var host = TestServerHost.Start(_root);
        try {
            var (status, body, nextCalled, headers) = await Ask(host, "GET", "/status.relatude.db");
            Assert.AreEqual(200, status);
            Assert.IsFalse(nextCalled, "nothing after the middleware should see the request");
            Assert.AreEqual("no-store", headers.CacheControl.ToString());
            using var json = JsonDocument.Parse(body);
            Assert.AreEqual("ok", json.RootElement.GetProperty("status").GetString());
            Assert.AreEqual("Open", json.RootElement.GetProperty("database").GetString());
            Assert.AreEqual(StatusEndpoint.Version, json.RootElement.GetProperty("version").GetString());
            Assert.IsTrue(json.RootElement.TryGetProperty("serverTimeUtc", out _));
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Head_AnswersTheStatusWithoutABody() {
        var host = TestServerHost.Start(_root);
        try {
            var (status, body, nextCalled, _) = await Ask(host, "HEAD", "/STATUS.relatude.db/");
            Assert.AreEqual(200, status);
            Assert.IsFalse(nextCalled);
            Assert.AreEqual(string.Empty, body);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task OtherMethodsAndPaths_PassThrough() {
        var host = TestServerHost.Start(_root);
        try {
            Assert.IsTrue((await Ask(host, "POST", "/status.relatude.db")).NextCalled);
            Assert.IsTrue((await Ask(host, "GET", "/status.relatude.db/more")).NextCalled);
            Assert.IsTrue((await Ask(host, "GET", "/")).NextCalled);
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ClosedDatabase_Answers503() {
        var host = TestServerHost.Start(_root);
        try {
            host.Server.DefaultContainer!.CloseIfOpen();
            var (status, body, nextCalled, _) = await Ask(host, "GET", "/status.relatude.db");
            Assert.AreEqual(503, status);
            Assert.IsFalse(nextCalled);
            using var json = JsonDocument.Parse(body);
            Assert.AreEqual("closed", json.RootElement.GetProperty("status").GetString());
            Assert.AreEqual("Closed", json.RootElement.GetProperty("database").GetString());
        } finally {
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ShuttingDown_StillAnswers() {
        var host = TestServerHost.Start(_root);
        try {
            host.Server.Shutdown();
            var (status, body, _, _) = await Ask(host, "GET", "/status.relatude.db");
            Assert.AreEqual(503, status);
            using var json = JsonDocument.Parse(body);
            Assert.AreEqual("shutting-down", json.RootElement.GetProperty("status").GetString());
        } finally {
            await host.DisposeAsync();
        }
    }

    static async Task<(int Status, string Body, bool NextCalled, IHeaderDictionary Headers)> Ask(TestServerHost host, string method, string path) {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        var body = new MemoryStream();
        context.Response.Body = body;
        var nextCalled = false;
        await host.Server.Authentication.StartupProgressBarMiddleware(context, () => {
            nextCalled = true;
            return Task.CompletedTask;
        });
        return (context.Response.StatusCode, System.Text.Encoding.UTF8.GetString(body.ToArray()), nextCalled, context.Response.Headers);
    }
}
