namespace Relatude.DB.NodeServer.EventHub;
public class EventSubscriptions {
    readonly Dictionary<Guid, EventSubscription> _eventSubscriptions = [];
    public void EnqueueToMatchingSubscriptions(EventData eventData) {
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
    public Guid CreateSubscription(params string[] events) {
        var subscription = new EventSubscription { EventNames = new HashSet<string>(events) };
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


