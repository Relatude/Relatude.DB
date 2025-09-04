
namespace Relatude.DB.NodeServer;
public class EventData(string name, string data) {
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Timestamp { get; } = DateTime.UtcNow;
    public string Name { get; } = name;
    public string Data { get; } = data;
}
public class EventSubscription(string[] events) {
    public Guid SubscriptionId { get; } = Guid.NewGuid();
    public string[] Events { get; } = events;
}

public static class UIEventHub {
    // Server-Sent Events endpoint for UI events:
    static Dictionary<Guid, Queue<EventData>> _eventSubscribers = new();
    internal static async Task Serve(HttpContext context, string[] events) {

        var response = context.Response;
        var headers = response.Headers;
        var cancellationToken = context.RequestAborted;

        headers.Append("Content-Type", "text/event-stream");
        headers.Append("Cache-Control", "no-cache");
        headers.Append("Connection", "keep-alive");
        headers.Append("X-Accel-Buffering", "no"); // Disable buffering for nginx

        var eventQueue = new Queue<(int Id, string Data)>();
        try {
            // Keep the connection alive and send new events
            while (!cancellationToken.IsCancellationRequested) {
                //while (eventQueue.Count > 0) {
                //    var (Id, Data) = eventQueue.Dequeue();
                //    await response.WriteAsync($"id: {Id}\n");
                //    await response.WriteAsync($"data: {Data}\n\n");
                //    await response.Body.FlushAsync(cancellationToken);
                //}
                //await Task.Delay(1000, cancellationToken); // Adjust the delay as needed
            }
        } catch (OperationCanceledException) {
            // Client disconnected
        }
    }
}
