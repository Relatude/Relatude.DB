
using System.Text.Json;
using System.Text;

namespace Relatude.DB.NodeServer;
public class EventData(string name, object data, TimeSpan? maxAge = null) {
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Timestamp { get; } = DateTime.UtcNow;
    public TimeSpan MaxAge { get; set; } = maxAge ?? TimeSpan.FromSeconds(60);
    public string Name { get; } = name;
    public object Data { get; } = data;
}
public class EventSubscription(string[] events) {
    public Guid SubscriptionId { get; } = Guid.NewGuid();
    public HashSet<string> EventNames { get; } = new(events);
    public LinkedList<EventData> EventQueue { get; } = new();
}
public class EventSubscriptions {
    readonly Dictionary<Guid, EventSubscription> _eventSubscriptions = [];
    public void Enqueue(EventData eventData) {
        lock (_eventSubscriptions) {
            foreach (var subscription in _eventSubscriptions.Values) {
                if (subscription.EventNames.Contains(eventData.Name)) {
                    subscription.EventQueue.AddLast(eventData);
                }
            }
        }
    }
    public bool IsSubscribing(Guid subscriptionId) {
        lock (_eventSubscriptions) {
            return _eventSubscriptions.ContainsKey(subscriptionId);
        }
    }
    public void Deactivate(Guid subscriptionId) {
        lock (_eventSubscriptions) {
            _eventSubscriptions.Remove(subscriptionId);
        }
    }
    public EventSubscription[] GetAllSubscriptions() {
        lock (_eventSubscriptions) {
            return [.. _eventSubscriptions.Values];
        }
    }
    public EventData? Dequeue(Guid subscriptionId) {
        lock (_eventSubscriptions) {
            if (_eventSubscriptions.TryGetValue(subscriptionId, out var subscription)) {
                var now = DateTime.UtcNow;
                var node = subscription.EventQueue.First;
                while (node != null) {
                    subscription.EventQueue.RemoveFirst();
                    var notExpired = now - node.Value.Timestamp <= node.Value.MaxAge;
                    if (notExpired) return node.Value;
                    node = subscription.EventQueue.First;
                }
            }
            return null;
        }
    }
    public Guid CreateSubscription(string[] events) {
        var subscription = new EventSubscription(events);
        lock (_eventSubscriptions) {
            _eventSubscriptions[subscription.SubscriptionId] = subscription;
        }
        return subscription.SubscriptionId;
    }
    public void CancelSubscription(Guid subscriptionId) {
        lock (_eventSubscriptions) {
            _eventSubscriptions.Remove(subscriptionId);
        }
    }
    public void ChangeSubscription(Guid subscriptionId, string[] events) {
        lock (_eventSubscriptions) {
            if (_eventSubscriptions.TryGetValue(subscriptionId, out var subscription)) {
                subscription.EventNames.Clear();
                foreach (var e in events) subscription.EventNames.Add(e);
            }
        }
    }
    public int Count() {
        lock (_eventSubscriptions) {
            return _eventSubscriptions.Count;
        }
    }
    DateTime _lastCheckExpired = DateTime.MinValue;
    public void ClearExpired() {
        lock (_eventSubscriptions) {
            if (DateTime.UtcNow - _lastCheckExpired > TimeSpan.FromMinutes(1)) {
                _lastCheckExpired = DateTime.UtcNow;
                clearExpired();
            }
        }
    }
    void clearExpired() {
        var now = DateTime.UtcNow;
        foreach (var subscription in _eventSubscriptions.Values) {
            var node = subscription.EventQueue.First;
            while (node != null) {
                var next = node.Next;
                if (now - node.Value.Timestamp > node.Value.MaxAge) {
                    subscription.EventQueue.Remove(node);
                }
                node = next;
            }
        }
    }
}
public static class ServerEventHub {
    static EventSubscriptions _subscriptions = new();
    public static void Publish(string name, object data, TimeSpan? maxAge = null) => _subscriptions.Enqueue(new(name, data, maxAge));
    internal static void ChangeSubscription(Guid subscriptionId, string[] events) => _subscriptions.ChangeSubscription(subscriptionId, events);
    internal static void Unsubscribe(Guid subscriptionId) => _subscriptions.Deactivate(subscriptionId);
    internal static EventSubscription[] GetAllSubscriptions() => _subscriptions.GetAllSubscriptions();
    internal static int SubscriptionCount() => _subscriptions.Count();
    internal static async Task Subscribe(HttpContext context, string[] events) {

        var response = context.Response;
        var headers = response.Headers;
        var cancellationToken = context.RequestAborted;

        headers.Append("Content-Type", "text/event-stream");
        headers.Append("Cache-Control", "no-cache");
        headers.Append("Connection", "keep-alive");
        headers.Append("X-Accel-Buffering", "no"); // Disable buffering for nginx

        var subscriptionId = _subscriptions.CreateSubscription(events);
        try {
            await writeEvent(context, new EventData("subscriptionId", subscriptionId.ToString()));
            while (!cancellationToken.IsCancellationRequested) {
                var eventData = _subscriptions.Dequeue(subscriptionId);
                if (eventData != null) {
                    await writeEvent(context, eventData);
                } else {
                    if (!_subscriptions.IsSubscribing(subscriptionId)) break;
                    await Task.Delay(1000, cancellationToken);
                    _subscriptions.ClearExpired();
                }
            }
        } catch (Exception error) {
            Console.WriteLine("SSE Error: " + error.Message + "\n" + error.StackTrace + "\n");
        } finally {
            _subscriptions.CancelSubscription(subscriptionId);
        }
    }
    static async Task writeEvent(HttpContext context, EventData e) {
        var response = context.Response;
        var stringData = buildEvent(e.Data, e.Name);
        await response.WriteAsync(stringData, context.RequestAborted);
        await response.WriteAsync($"\n\n");
        await response.Body.FlushAsync(context.RequestAborted);
    }
    static string buildEvent(object? dataObject, string? eventName = null, string? id = null, int? retryMs = null, bool pretty = false) {
        var json = System.Text.Json.JsonSerializer.Serialize(dataObject, new System.Text.Json.JsonSerializerOptions {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Converters = {
                new Relatude.DB.Nodes.RelationJsonConverter()
            }
        });
        var sb = new StringBuilder(json.Length + 64);
        if (retryMs is int r) sb.Append("retry: ").Append(r).Append('\n');
        if (!string.IsNullOrEmpty(id)) sb.Append("id: ").Append(id).Append('\n');
        if (!string.IsNullOrEmpty(eventName)) sb.Append("event: ").Append(eventName).Append('\n');

        using var reader = new StringReader(json);
        string? line;
        while ((line = reader.ReadLine()) != null) {
            sb.Append("data: ").Append(line).Append('\n');
        }
        sb.Append('\n');
        return sb.ToString();
    }
}

}
