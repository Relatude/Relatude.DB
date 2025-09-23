
using Microsoft.AspNetCore.Http.Features;
using Relatude.DB.NodeServer.Json;
using System.Text;
using System.Text.Json;
namespace Relatude.DB.NodeServer.EventHub;
public static class ServerEventHub {
    static EventSubscriptions _subscriptions = new();
    public static void Publish<T, K>(string name, K data, TimeSpan? maxAge = null) => _subscriptions.EnqueueToMatchingSubscriptions(new EventData<K>(name, data, maxAge));
    public static void Publish<T, K>(string name, Func<EventSubscription<T>, K> data, TimeSpan? maxAge = null) => _subscriptions.EnqueueToMatchingSubscriptions(new EventDataBuilder<T, K>(name, data, maxAge));
    public static void ChangeSubscription(Guid subscriptionId, params string[] events) => _subscriptions.ChangeSubscription(subscriptionId, events);
    public static void Unsubscribe(Guid subscriptionId) => _subscriptions.Deactivate(subscriptionId);
    public static IEventSubscription[] GetAllSubscriptions() => _subscriptions.GetAllSubscriptions();
    public static int SubscriptionCount() => _subscriptions.Count();
    public static async Task Subscribe<T>(HttpContext context, T subscriberData, params string[] events) {

        var response = context.Response;
        var headers = response.Headers;
        var cancellation = context.RequestAborted;

        headers.Append("Content-Type", "text/event-stream");
        headers.Append("Cache-Control", "no-cache");
        if (context.Request.Protocol == "HTTP/1.1") headers.Append("Connection", "keep-alive"); // not needed for HTTP/2 and later:
        headers.Append("X-Accel-Buffering", "no"); // Disable buffering for nginx

        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var subscriptionId = _subscriptions.CreateSubscription(subscriberData, events);
        try {
            await writeEvent(response, cancellation, new EventData<string>("subscriptionId", subscriptionId.ToString()));
            Console.WriteLine("SSE client connected, subscriptionId: " + subscriptionId + ". Connections: " + _subscriptions.Count().ToString("N0"));
            while (!cancellation.IsCancellationRequested) {
                var eventData = _subscriptions.Dequeue(subscriptionId);
                if (eventData != null) {
                    await writeEvent(response, cancellation, eventData);
                } else {
                    if (!_subscriptions.IsSubscribing(subscriptionId)) break;
                    await Task.Delay(500, cancellation);
                    _subscriptions.ClearExpired();
                }
            }
        } catch (TaskCanceledException) {
            Console.WriteLine("SSE client disconnected, subscriptionId: " + subscriptionId + ". Connections: " + (_subscriptions.Count() - 1).ToString("N0"));
        } catch (Exception error) {
            Console.WriteLine("SSE Error: " + error.Message + "\n" + error.StackTrace + "\n");
        } finally {
            _subscriptions.CancelSubscription(subscriptionId);
        }
    }
    static async Task writeEvent(HttpResponse response, CancellationToken cancellation, IEventData e) {
        var stringData = buildEvent(e);
        await response.WriteAsync(stringData, cancellation);
        await response.Body.FlushAsync(cancellation);
    }
    static string buildEvent(object? dataObject, string? eventName = null, string? id = null, int? retryMs = null) {
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

