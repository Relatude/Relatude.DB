
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
namespace Relatude.DB.NodeServer.EventHub;
public static class ServerEventHub {
    static EventSubscriptions _subscriptions = new();
    public static void Publish(string name, object data, TimeSpan? maxAge = null) => _subscriptions.EnqueueToMatchingSubscriptions(new(name, data, maxAge));
    public static void ChangeSubscription(Guid subscriptionId, string[] events) => _subscriptions.ChangeSubscription(subscriptionId, events);
    public static void Unsubscribe(Guid subscriptionId) => _subscriptions.Deactivate(subscriptionId);
    public static EventSubscription[] GetAllSubscriptions() => _subscriptions.GetAllSubscriptions();
    public static int SubscriptionCount() => _subscriptions.Count();
    public static async Task Subscribe(HttpContext context) {

        var response = context.Response;
        var headers = response.Headers;
        var cancellation = context.RequestAborted;

        headers.Append("Content-Type", "text/event-stream");
        headers.Append("Cache-Control", "no-cache");
        if (context.Request.Protocol == "HTTP/1.1") headers.Append("Connection", "keep-alive"); // not needed for HTTP/2 and later:
        headers.Append("X-Accel-Buffering", "no"); // Disable buffering for nginx

        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var subscriptionId = _subscriptions.CreateSubscription();
        try {
            await writeEvent(response, cancellation, new("subscriptionId", subscriptionId.ToString()));
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
    static async Task writeEvent(HttpResponse response, CancellationToken cancellation, EventData e) {
        var stringData = buildEvent(e);
        await response.WriteAsync(stringData, cancellation);
        await response.Body.FlushAsync(cancellation);
    }
    static string buildEvent(object? dataObject, string? eventName = null, string? id = null, int? retryMs = null) {
        var json = JsonSerializer.Serialize(dataObject, GlobalExtensions.DefaultJsonHttpOptions);
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

