using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Relatude.DB.IPLookup;

namespace Relatude.DB.NodeServer;

/// <summary>
/// The server's info path (<c>InfoUrlPath</c>, <see cref="BotCheck.DefaultUrlPath"/> unless the
/// settings say otherwise): what the application's pages need of the Relatude IP lookup service,
/// served by this server so a browser never talks to any other.
/// <code>
///   GET  /-info/bot.js                  the bot check, as a file (BotCheck.LoaderScript)
///   GET  /-info/botd.js?v=…             Fingerprint's BotD, which it imports
///   POST /-info/report?isBot=…          the browser's answer, for its address
/// </code>
/// The two scripts come from the IP lookup service, through the default database's provider, which
/// keeps them an hour at a time (<see cref="IIPLookupProvider.GetBotCheckScriptsAsync"/>): the service
/// is asked by this server, not by the browsers, and seldom. Until the scripts have come once, and
/// while there is no open default database, the two are answered 503.
/// A report is for the address this server sees the browser by - behind a proxy or a load balancer
/// the application must call <c>UseForwardedHeaders</c> for that to be the browser's. An address's
/// report is passed on once in <see cref="RepeatAfter"/>, or again when it says bot after saying no
/// bot: the service keeps one answer an address, and a bot's sticks, so the reports of every other
/// page view say nothing new and are not sent. What is to be sent is queued, and a loop passes the
/// queue on to the service within a second, a thousand at a time, with the default database's IP
/// lookup provider (<see cref="IIPLookupProvider.ReportAsync"/>). The queue holds at most
/// <see cref="MaxQueued"/> reports; past that, and for a report another site's page posts here, the
/// answer is a refusal and nothing is kept. A batch the service does not take is let go, and the
/// default database's log says why, once a minute at most.
/// <para>The path is read at every request, so a change to it applies at once. Anything else under
/// it goes on to the rest of the application.</para>
/// </summary>
public sealed class InfoEndpoint {
    /// <summary>The most reports waiting to be passed on.</summary>
    public const int MaxQueued = 10_000;
    /// <summary>How long an address's report, once passed on, is not passed on again unless it changes to bot.</summary>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(10);

    const int _batchSize = 1000;
    const int _maxRemembered = 100_000;
    static readonly TimeSpan _gather = TimeSpan.FromSeconds(1);
    static readonly TimeSpan _waitForDatabase = TimeSpan.FromSeconds(30);

    readonly RelatudeDBServer _server;
    readonly Channel<BotReport> _queue = Channel.CreateBounded<BotReport>(new BoundedChannelOptions(MaxQueued) { SingleReader = true });
    // what each address last said that was passed on, or is on its way, and when
    readonly object _passedLock = new();
    readonly Dictionary<IPAddress, (bool IsBot, DateTime Utc)> _passed = new();
    int _sending;
    DateTime _lastWarning;
    long _passedOn, _repeated, _dropped;
    // the two scripts as bytes, made once for each copy the provider hands out
    (BotCheckScripts Source, byte[] Loader, byte[] BotD)? _served;

    internal InfoEndpoint(RelatudeDBServer server) {
        _server = server;
    }

    /// <summary>Reports passed on to the service, not sent as they said nothing new, and let go here, since the server started.</summary>
    public (long PassedOn, long Repeated, long Dropped) Counts => (Interlocked.Read(ref _passedOn), Interlocked.Read(ref _repeated), Interlocked.Read(ref _dropped));

    /// <summary>Answers the routes under the info path; everything else passes through.</summary>
    public async Task Middleware(HttpContext http, Func<Task> next) {
        var root = BotCheck.UrlPath(_server.Settings.InfoUrlPath);
        var path = http.Request.Path.Value;
        if (path == null || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
            await next();
            return;
        }
        var method = http.Request.Method;
        var readable = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        switch (path[root.Length..]) {
            case BotCheck.LoaderFile when readable: {
                    if (await scriptsAsync(http) is not { } served) return;
                    await scriptAsync(http, served.Loader, "public, max-age=3600");
                    return;
                }
            case BotCheck.BotDFile when readable: {
                    if (await scriptsAsync(http) is not { } served) return;
                    // imported with its version in the address, so a browser may keep it as long as it likes
                    var current = http.Request.Query["v"] == served.Source.Version;
                    await scriptAsync(http, served.BotD, current ? "public, max-age=31536000, immutable" : "public, max-age=3600");
                    return;
                }
            case BotCheck.ReportRoute when HttpMethods.IsPost(method):
                report(http);
                return;
        }
        await next();
    }

    /// <summary>The scripts from the default database's provider, as bytes; null, with a 503 written, when there are none to be had.</summary>
    async Task<(BotCheckScripts Source, byte[] Loader, byte[] BotD)?> scriptsAsync(HttpContext http) {
        try {
            if (_server.DefaultContainer is not { } container || !container.IsOpen() || container.Store is not { } store || !store.Services.HasIPLookup)
                throw new InvalidOperationException("There is no open default database to serve the bot check with.");
            var scripts = await store.Services.IPLookup.GetBotCheckScriptsAsync(http.RequestAborted);
            var served = _served;
            if (served == null || !ReferenceEquals(served.Value.Source, scripts)) {
                served = (scripts, Encoding.UTF8.GetBytes(BotCheck.LoaderScript(scripts)), Encoding.UTF8.GetBytes(scripts.BotD));
                _served = served;
            }
            return served;
        } catch (Exception) when (!http.RequestAborted.IsCancellationRequested) {
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            http.Response.Headers.CacheControl = "no-store";
            return null;
        }
    }

    static async Task scriptAsync(HttpContext http, byte[] script, string cacheControl) {
        http.Response.ContentType = "text/javascript; charset=utf-8";
        http.Response.Headers.CacheControl = cacheControl;
        http.Response.ContentLength = script.Length;
        if (!HttpMethods.IsHead(http.Request.Method)) await http.Response.Body.WriteAsync(script, http.RequestAborted);
    }

    void report(HttpContext http) {
        http.Response.Headers.CacheControl = "no-store";
        // a beacon another site's page sends here would speak for its visitors, not this site's
        if (string.Equals(http.Request.Headers["Sec-Fetch-Site"], "cross-site", StringComparison.OrdinalIgnoreCase)) {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!bool.TryParse(http.Request.Query["isBot"].ToString(), out var isBot) || http.Connection.RemoteIpAddress is not { } remote) {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        http.Response.StatusCode = StatusCodes.Status204NoContent;
        var address = canonical(remote);
        if (!sayNew(address, isBot)) {
            Interlocked.Increment(ref _repeated);
            return;
        }
        if (!_queue.Writer.TryWrite(new BotReport(address.ToString(), isBot))) {
            forget(address);
            Interlocked.Increment(ref _dropped);
            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        if (Interlocked.CompareExchange(ref _sending, 1, 0) == 0) _ = Task.Run(sendLoopAsync);
    }

    /// <summary>
    /// Whether a report says something the service has not been told lately: the address's first in
    /// <see cref="RepeatAfter"/>, or a bot after no bot. With more addresses than <c>_maxRemembered</c>
    /// remembered, those passed on longer ago are forgotten, and all of them when that is not enough.
    /// </summary>
    bool sayNew(IPAddress address, bool isBot) {
        var now = DateTime.UtcNow;
        lock (_passedLock) {
            if (_passed.TryGetValue(address, out var last) && now - last.Utc < RepeatAfter && (last.IsBot || !isBot)) return false;
            if (_passed.Count >= _maxRemembered) {
                foreach (var old in _passed.Where(p => now - p.Value.Utc >= RepeatAfter).Select(p => p.Key).ToArray()) _passed.Remove(old);
                if (_passed.Count >= _maxRemembered) _passed.Clear();
            }
            _passed[address] = (isBot, now);
            return true;
        }
    }

    void forget(IPAddress address) {
        lock (_passedLock) _passed.Remove(address);
    }

    /// <summary>
    /// Passes the queue on as long as there is anything in it: whatever came within a second of the
    /// first, at most a thousand, in one call.
    /// </summary>
    async Task sendLoopAsync() {
        var batch = new List<BotReport>(_batchSize);
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync()) {
            var until = DateTime.UtcNow + _gather;
            while (batch.Count < _batchSize) {
                while (batch.Count < _batchSize && reader.TryRead(out var report)) batch.Add(report);
                var left = until - DateTime.UtcNow;
                if (batch.Count >= _batchSize || left <= TimeSpan.Zero) break;
                using var gathering = new CancellationTokenSource(left);
                try {
                    if (!await reader.WaitToReadAsync(gathering.Token)) break;
                } catch (OperationCanceledException) {
                    break;
                }
            }
            await sendAsync(batch);
            batch.Clear();
        }
    }

    async Task sendAsync(List<BotReport> batch) {
        var store = await openStoreAsync();
        if (store == null || !store.Services.HasIPLookup) {
            letGo(batch);
            return;
        }
        try {
            await store.Services.IPLookup.ReportAsync(batch);
            Interlocked.Add(ref _passedOn, batch.Count);
        } catch (Exception error) {
            letGo(batch);
            if (DateTime.UtcNow - _lastWarning > TimeSpan.FromMinutes(1)) {
                _lastWarning = DateTime.UtcNow;
                store.Datastore.LogWarning($"{batch.Count} bot check reports from {BotCheck.UrlPath(_server.Settings.InfoUrlPath)} could not be passed on to the IP lookup service: {error.Message}");
            }
        }
    }

    // a batch that did not reach the service: its addresses are sent again at their next report
    void letGo(List<BotReport> batch) {
        Interlocked.Add(ref _dropped, batch.Count);
        foreach (var report in batch) forget(IPAddress.Parse(report.Ip));
    }

    // the default database, once it is open: reports made while it opens wait for it, half a minute at most
    async Task<Nodes.NodeStore?> openStoreAsync() {
        var waited = TimeSpan.Zero;
        while (true) {
            if (_server.DefaultContainer is { } container && container.IsOpen()) return container.Store;
            if (waited >= _waitForDatabase) return null;
            await Task.Delay(TimeSpan.FromSeconds(2));
            waited += TimeSpan.FromSeconds(2);
        }
    }

    /// <summary>One form for one address: an IPv4-mapped IPv6 address as IPv4, and no scope.</summary>
    static IPAddress canonical(IPAddress address) {
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) return new IPAddress(address.GetAddressBytes());
        return address;
    }
}
