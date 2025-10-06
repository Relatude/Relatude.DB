
using Microsoft.AspNetCore.Http.Features;
using Relatude.DB.NodeServer.Json;
using System.Text;
using System.Text.Json;
namespace Relatude.DB.NodeServer.EventHub;
internal class ServerEventHub {
    ServerSideConnectionDirectory _directory = new(); // thread-safe
    List<TriggerAndDueTime> _triggers = [];
    Timer _triggerPulseTimer;
    int _minimumUpdateIntervalMs = 100;
    RelatudeDBServer _server;
    public ServerEventHub(RelatudeDBServer server) {
        _triggerPulseTimer = new Timer((o) => triggerPulse(), null, 2000, 1000);
        _server = server;
    }
    void triggerPulse() {
        foreach (var trigger in _triggers.Where(t => t.DueTime <= DateTime.UtcNow)) {
            if (!_directory.AnySubscribers(trigger.Trigger.EventName)) continue;
            var filters = _directory.GetFiltersOfSubscribers(trigger.Trigger.EventName);
            var bullets = trigger.Trigger.CollectPayloads(_server, filters, out int msNextCollect);
            trigger.DueTime = DateTime.UtcNow.AddMilliseconds(msNextCollect);
            if (bullets == null) continue;
            foreach (var b in bullets) _directory.EnqueueToMatchingSubscriptions(trigger.Trigger.EventName, b);
        }
        var nextDueTime = _triggers.Min(t => t.DueTime);
        var dueMs = (int)(nextDueTime - DateTime.UtcNow).TotalMilliseconds;
        dueMs = Math.Max(dueMs, _minimumUpdateIntervalMs); // at least 100ms
        _triggerPulseTimer.Change(dueMs, 1000);
    }
    public void RegisterTrigger(IEventTrigger trigger) {
        _triggers.Add(new TriggerAndDueTime(trigger, DateTime.UtcNow));
    }
    public async Task Connect(HttpContext context) {

        var response = context.Response;
        var headers = response.Headers;
        var cancellation = context.RequestAborted;

        headers.Append("Content-Type", "text/event-stream");
        headers.Append("Cache-Control", "no-cache");
        if (context.Request.Protocol == "HTTP/1.1") headers.Append("Connection", "keep-alive"); // not needed for HTTP/2 and later:
        headers.Append("X-Accel-Buffering", "no"); // Disable buffering for nginx

        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var connectionId = _directory.Connect();
        try {
            await writeEvent(response, cancellation, new ServerEventData("connectionId", (filter, ctx) => connectionId.ToString()));
            Console.WriteLine("SSE client connected, connectionId: " + connectionId + ". Connections: " + _directory.Count().ToString("N0"));
            while (!cancellation.IsCancellationRequested) {
                var eventData = _directory.Dequeue(connectionId);
                if (eventData != null) {
                    await writeEvent(response, cancellation, eventData);
                } else {
                    if (!_directory.IsConnected(connectionId)) break;
                    await Task.Delay(_minimumUpdateIntervalMs, cancellation);
                    _directory.ClearExpiredEvents();
                }
            }
        } catch (TaskCanceledException) {
            Console.WriteLine("SSE client disconnected, connectionId: " + connectionId + ". Connections: " + (_directory.Count() - 1).ToString("N0"));
        } catch (Exception error) {
            Console.WriteLine("SSE Error: " + error.Message + "\n" + error.StackTrace + "\n");
        } finally {
            _directory.Disconnect(connectionId);
        }
    }
    public void Disconnect(Guid connectionId) => _directory.Disconnect(connectionId);
    public void Subscribe(Guid connectionId, string name, string? filter) => _directory.Subscribe(connectionId, name, filter);
    public void Unsubscribe(Guid connectionId, string? name, string? filter) => _directory.Unsubscribe(connectionId, name, filter);
    public int SubscriptionCount() => _directory.Count();
    async Task writeEvent(HttpResponse response, CancellationToken cancellation, ServerEventData e) {
        var stringData = buildEvent(e);
        await response.WriteAsync(stringData, cancellation);
        await response.Body.FlushAsync(cancellation);
    }
    string buildEvent(object? dataObject, string? eventName = null, string? id = null, int? retryMs = null) {
        var json = JsonSerializer.Serialize(dataObject, RelatudeDBJsonOptions.Default);
        var sb = new StringBuilder(json.Length + 64);
        if (retryMs is int r) sb.Append("retry: ").Append(r).Append('\n');
        if (!string.IsNullOrEmpty(id)) sb.Append("id: ").Append(id).Append('\n');
        if (!string.IsNullOrEmpty(eventName)) sb.Append("event: ").Append(eventName).Append('\n');
        // supporting multi-line JSON data:
        using var reader = new StringReader(json);
        string? line;
        while ((line = reader.ReadLine()) != null) {
            sb.Append("data: ").Append(line).Append('\n');
        }
        sb.Append('\n');
        return sb.ToString();
    }
}

