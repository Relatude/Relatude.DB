namespace Relatude.DB.NodeServer.EventHub;
/// <summary>
/// A thread-safe directory of server-side connections and their event subscriptions.
/// </summary>
public class ServerSideConnectionDirectory {
    class EventSubscription(string name, string? filter) {
        public string EventName { get; } = name;
        public string? Filter { get; } = filter;
    }
    class ServerSideConnection(IConnectionContext context) {
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public IConnectionContext Context { get; } = context;
        public List<EventSubscription> Subscriptions { get; } = [];
        public LinkedList<ServerEventData> EventQueue { get; } = [];
    }
    readonly Dictionary<Guid, ServerSideConnection> _serverSideConnection = [];
    public void EnqueueToMatchingSubscriptions(string eventName, EventPayload bullet) {
        // room for optimization....
        lock (_serverSideConnection) {
            foreach (var connection in _serverSideConnection.Values) {
                foreach (var subscription in connection.Subscriptions) {
                    if (subscription.EventName == eventName) {
                        if (subscription.Filter == null || subscription.Filter == bullet.Filter) {
                            connection.EventQueue.AddLast(new ServerEventData(eventName, bullet.GetData()));
                        }
                    }
                }
            }
        }
    }
    public bool IsConnected(Guid connectionId) {
        lock (_serverSideConnection) {
            return _serverSideConnection.ContainsKey(connectionId);
        }
    }
    public bool AnySubscribers(string eventName) {
        // room for optimization....
        lock (_serverSideConnection) {
            foreach (var connection in _serverSideConnection.Values) {
                if (connection.Subscriptions.Any(s => s.EventName == eventName)) return true;
            }
            return false;
        }
    }
    public string?[] GetFiltersOfSubscribers(string eventName) {
        // room for optimization....
        lock (_serverSideConnection) {
            var filters = new HashSet<string?>();
            foreach (var connection in _serverSideConnection.Values) {
                foreach (var subscription in connection.Subscriptions) {
                    if (subscription.EventName == eventName) {
                        filters.Add(subscription.Filter);
                    }
                }
            }
            return filters.ToArray();
        }
    }
    public ServerEventData? Dequeue(Guid connectionId) {
        lock (_serverSideConnection) {
            if (_serverSideConnection.TryGetValue(connectionId, out var connection)) {
                var now = DateTime.UtcNow;
                var node = connection.EventQueue.First;
                while (node != null) {
                    connection.EventQueue.RemoveFirst();
                    var notExpired = now - node.Value.Timestamp <= node.Value.MaxAge;
                    if (notExpired) return node.Value;
                    node = connection.EventQueue.First;
                }
            }
            return null;
        }
    }
    public Guid Connect() {
        var connection = new ServerSideConnection();
        lock (_serverSideConnection) {
            _serverSideConnection[connection.ConnectionId] = connection;
        }
        return connection.ConnectionId;
    }
    public void Disconnect(Guid connectionId) {
        lock (_serverSideConnection) {
            _serverSideConnection.Remove(connectionId);
        }
    }
    public void Subscribe(Guid connectionId, string name, string? filter) {
        lock (_serverSideConnection) {
            if (_serverSideConnection.TryGetValue(connectionId, out var connection)) {
                connection.Subscriptions.Add(new EventSubscription(name, filter));
            }
        }
    }
    public void Unsubscribe(Guid connectionId, string? name, string? filter) {
        lock (_serverSideConnection) {
            if (_serverSideConnection.TryGetValue(connectionId, out var connection)) {
                if (name == null) {
                    connection.Subscriptions.Clear();
                } else if (filter == null)
                    connection.Subscriptions.RemoveAll(s => s.EventName == name);
                else
                    connection.Subscriptions.RemoveAll(s => s.EventName == name && s.Filter == filter);
            }
        }
    }
    public int Count() {
        lock (_serverSideConnection) {
            return _serverSideConnection.Count;
        }
    }
    DateTime _lastCheckExpired = DateTime.MinValue;
    public void ClearExpiredEvents() {
        lock (_serverSideConnection) {
            if (DateTime.UtcNow - _lastCheckExpired > TimeSpan.FromMinutes(1)) {
                _lastCheckExpired = DateTime.UtcNow;
                clearExpiredEvents();
            }
        }
    }
    void clearExpiredEvents() {
        var now = DateTime.UtcNow;
        foreach (var connection in _serverSideConnection.Values) {
            var node = connection.EventQueue.First;
            while (node != null) {
                var next = node.Next;
                if (now - node.Value.Timestamp > node.Value.MaxAge) {
                    connection.EventQueue.Remove(node);
                }
                node = next;
            }
        }
    }
}


