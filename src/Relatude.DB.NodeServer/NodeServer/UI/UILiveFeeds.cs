using Relatude.DB.NodeServer.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Relatude.DB.NodeServer.UI;
/// <summary>
/// What the admin UI follows, followed on the server instead of asked for by the browser.
///
/// A page that shows something live - the dashboard counters, a queue draining, the system trace, a
/// log being watched - names the command that answers its question and how often it wants an answer,
/// and this runs that command on the tab's own <see cref="UIEventStream"/> connection until the page
/// goes away. Three things follow, and they are the reason it exists:
///   - the browser makes one request per subscription instead of one per interval, so a page left
///     open costs a connection rather than a request every second;
///   - a sample that says exactly what the last one said is not sent at all, so a quiet database is
///     quiet on the wire as well - which is most of them, most of the time;
///   - the interval is the server's, so a command that takes longer than the interval simply delays
///     the next sample rather than having another one arrive on top of it.
///
/// A feed is one command with one payload. Changing what a page asks for - another range, another
/// filter - is a new subscription and the old one is dropped, which is why the feed id comes from
/// the page rather than from here: the page knows when its question changed.
///
/// Commands run on the HttpContext the stream was opened with, so a feed is exactly as authorised as
/// the tab that asked for it, and no more. That authorisation is checked again on every pass: a
/// session that expires under an open page stops its feeds and says so, rather than answering a tab
/// whose cookie the command endpoint would now refuse.
/// Thread-safe.
/// </summary>
public sealed class UILiveFeeds {
    /// <summary>The fastest a feed may run, whatever was asked for: the slider's fastest step.</summary>
    const int minIntervalMs = 100;
    const int maxIntervalMs = 300000;
    /// <summary>A tab follows a handful of things; the cap is against a page that leaks subscriptions.</summary>
    const int maxFeedsPerTab = 64;
    /// <summary>How closely the loop can hit an interval. Well under the fastest step, and idle otherwise.</summary>
    const int tickMs = 25;
    readonly RelatudeDBServer _server;
    readonly UICommands _commands;
    readonly UIEventStream _events;
    readonly ConcurrentDictionary<Guid, Tab> _tabs = new();
    internal UILiveFeeds(RelatudeDBServer server, UICommands commands, UIEventStream events) {
        _server = server;
        _commands = commands;
        _events = events;
        events.Closed += Drop;
        commands.Register("stream-subscribe", ctx => subscribe(ctx.Payload<SubscribePayload>(), ctx));
        commands.Register("stream-unsubscribe", ctx => unsubscribe(ctx.Payload<UnsubscribePayload>()));
    }
    public int TabCount => _tabs.Count;
    /// <summary>
    /// Starts a feed and answers with its first sample, so subscribing is also the page's first load:
    /// one round trip where a poll would have been a subscribe and a fetch. A command that fails here
    /// fails the request, which is what the page expects of a first load - once it is running, a
    /// failure is an event on the stream instead.
    /// </summary>
    async Task<object?> subscribe(SubscribePayload p, UICommandContext ctx) {
        if (string.IsNullOrWhiteSpace(p.Feed)) throw new Exception("A feed needs an id. ");
        if (string.IsNullOrWhiteSpace(p.Type)) throw new Exception("A feed needs a command. ");
        if (!_events.TryGetHttpContext(p.ConnectionId, out var http)) throw new Exception("The event stream is not connected. ");
        var tab = _tabs.GetOrAdd(p.ConnectionId, id => new Tab(id));
        // what this subscription takes over from - the same page asking the same question at another
        // rate. It is dropped here rather than by the request the page sent to stop it, because those
        // two requests are independent and the stop can arrive after this one; doing it here is what
        // makes a change of rate safe whichever order they land in
        var replaced = !string.IsNullOrEmpty(p.Replaces) && p.Replaces != p.Feed && tab.Feeds.ContainsKey(p.Replaces);
        if (!tab.Feeds.ContainsKey(p.Feed) && tab.Feeds.Count - (replaced ? 1 : 0) >= maxFeedsPerTab) {
            throw new Exception("Too many live feeds on one connection. ");
        }
        var feed = new Feed(p.Feed, p.Type, p.Payload, Math.Clamp(p.EveryMs, minIntervalMs, maxIntervalMs));
        // the first sample is taken before the feed is registered: a command that throws leaves
        // nothing running - the one it replaces included - and the page is told why on the response
        // it is already waiting for
        var result = await _commands.Invoke(feed.Type, _server, http, feed.Payload);
        feed.Mark(hash(result));
        feed.Due = Environment.TickCount64 + feed.EveryMs;
        if (replaced) tab.Feeds.TryRemove(p.Replaces!, out _);
        tab.Feeds[feed.Id] = feed;
        start(tab);
        return result;
    }
    object unsubscribe(UnsubscribePayload p) {
        var stopped = _tabs.TryGetValue(p.ConnectionId, out var tab) && tab.Feeds.TryRemove(p.Feed, out _);
        // an answer rather than nothing at all: the command endpoint replies in JSON, and a body that
        // is not JSON is what every caller here would choke on. A feed that was already gone - taken
        // over by the subscription that replaced it - is not an error, so it says so instead
        return new { Stopped = stopped };
    }
    /// <summary>Everything this tab was following, dropped: the stream it was pushed on is gone.</summary>
    public void Drop(Guid connectionId) {
        if (!_tabs.TryRemove(connectionId, out var tab)) return;
        tab.Cancel.Cancel();
        tab.Cancel.Dispose();
    }
    void start(Tab tab) {
        if (tab.Running) return;
        lock (tab) {
            if (tab.Running) return;
            tab.Running = true;
        }
        _ = Task.Run(() => run(tab));
    }
    /// <summary>
    /// One loop per tab rather than one per feed: the feeds of a tab are sampled one after the other,
    /// so a page following four things asks the server for one at a time - the same order of work a
    /// polling page made, without the four requests.
    /// </summary>
    async Task run(Tab tab) {
        var token = tab.Cancel.Token;
        try {
            while (!token.IsCancellationRequested) {
                await Task.Delay(tickMs, token);
                if (tab.Feeds.IsEmpty) continue; // a page between subscriptions: the tab is kept, the loop idles
                if (!_events.TryGetHttpContext(tab.Id, out var http)) break; // the stream went away
                if (!_server.Authentication.IsLoggedIn(http)) {
                    // the session expired under an open page: the same 401 a command would have met,
                    // said once, so the UI can drop to the login screen instead of going quiet
                    _events.Send(tab.Id, "unauthorized", null);
                    break;
                }
                try {
                    foreach (var feed in tab.Feeds.Values) {
                        if (token.IsCancellationRequested) break;
                        if (feed.Due > Environment.TickCount64) continue;
                        await sample(tab, feed, http);
                        // measured from the end of the sample, so a command slower than the interval
                        // spaces itself out instead of running back to back
                        feed.Due = Environment.TickCount64 + feed.EveryMs;
                    }
                } catch (Exception error) when (error is not OperationCanceledException) {
                    // one bad pass is one bad pass: a page that was following something must not be
                    // left following nothing because of it
                    RelatudeDBServer.Trace("UI live feed pass failed: " + error.Message);
                }
            }
        } catch (OperationCanceledException) { // the tab was dropped
        } catch (Exception error) {
            RelatudeDBServer.Trace("UI live feed error: " + error.Message);
        } finally {
            // the tab itself is kept: only the connection closing removes it (see Drop), so a
            // subscription that arrives after this loop ended starts a new one rather than landing
            // in a tab nothing is reading
            tab.Running = false;
        }
    }
    async Task sample(Tab tab, Feed feed, HttpContext http) {
        object? result;
        try {
            result = await _commands.Invoke(feed.Type, _server, http, feed.Payload);
        } catch (Exception error) {
            // reported the way a sample is, so it is said once rather than on every pass: a database
            // that is closed under a page fails the same way until it is opened again
            if (feed.Mark(hash("!" + error.Message))) _events.Send(tab.Id, "feed", new { Feed = feed.Id, Error = error.Message });
            return;
        }
        if (!feed.Mark(hash(result))) return; // the same answer as last time: the page already has it
        _events.Send(tab.Id, "feed", new { Feed = feed.Id, Data = result });
    }
    /// <summary>
    /// What a sample says, in sixteen bytes. Hashed rather than kept, because keeping it would mean
    /// holding a copy of every log page and every dashboard on the server for as long as a tab is
    /// open. The serialization is thrown away and done again when the sample is actually sent, which
    /// costs a fraction of what the command itself cost to answer.
    /// </summary>
    static byte[] hash(object? value) {
        var json = JsonSerializer.Serialize(value, RelatudeDBJsonOptions.SSE);
        return MD5.HashData(Encoding.UTF8.GetBytes(json));
    }
    sealed class Tab(Guid id) {
        public Guid Id { get; } = id;
        public ConcurrentDictionary<string, Feed> Feeds { get; } = new();
        public CancellationTokenSource Cancel { get; } = new();
        public bool Running { get; set; }
    }
    sealed class Feed(string id, string type, JsonElement? payload, int everyMs) {
        public string Id { get; } = id;
        public string Type { get; } = type;
        public JsonElement? Payload { get; } = payload;
        public int EveryMs { get; } = everyMs;
        /// <summary>When this feed is next due, on <see cref="Environment.TickCount64"/>.</summary>
        public long Due { get; set; }
        byte[]? _last;
        /// <summary>Records what was just sampled, and says whether it differs from the last one.</summary>
        public bool Mark(byte[] digest) {
            var changed = _last == null || !_last.AsSpan().SequenceEqual(digest);
            _last = digest;
            return changed;
        }
    }
    sealed record SubscribePayload(Guid ConnectionId, string Feed, string Type, JsonElement? Payload, int EveryMs, string? Replaces = null);
    sealed record UnsubscribePayload(Guid ConnectionId, string Feed);
}
