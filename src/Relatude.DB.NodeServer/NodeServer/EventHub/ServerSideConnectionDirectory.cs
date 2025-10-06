namespace Relatude.DB.NodeServer.EventHub;
/// <summary>
/// A thread-safe directory of server-side connections and their event subscriptions.
/// </summary>
public class ServerSideConnectionDirectory {
    class ServerSideConnection(EventContext context) {
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public EventContext Context { get; } = context;
        public List<EventSubscription> Subscriptions { get; } = [];
        public LinkedList<ServerEventData> EventQueue { get; } = [];
    }
    readonly Dictionary<Guid, ServerSideConnection> _serverSideConnection = [];
    public void EnqueueEvent(string eventName, EventDataFactory factory) {
        // room for optimization....
        lock (_serverSideConnection) {
            foreach (var connection in _serverSideConnection.Values) {
                foreach (var subscription in connection.Subscriptions) {
                    if (subscription.EventName == eventName) {
                        if (subscription.Filter == null || subscription.Filter == factory.Filter) {
                            var payload = factory.GetPayload(connection.Context);
                            connection.EventQueue.AddLast(new ServerEventData(eventName, factory.Filter, payload));
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
    public string?[] GetFiltersOfSubscribers(Guid connectionId, string eventName) {
        // room for optimization....
        lock (_serverSideConnection) {
            if (_serverSideConnection.TryGetValue(connectionId, out var connection)) {
                var filters = new HashSet<string?>();
                foreach (var subscription in connection.Subscriptions) {
                    if (subscription.EventName == eventName) {
                        filters.Add(subscription.Filter);
                    }
                }
                return [.. filters];
            }
            return [];
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
    public Guid Connect(EventContext ctx, out int count) {
        var connection = new ServerSideConnection(ctx);
        lock (_serverSideConnection) {
            _serverSideConnection[connection.ConnectionId] = connection;
            count = _serverSideConnection.Count;
        }
        return connection.ConnectionId;
    }
    public int Disconnect(Guid connectionId) {
        lock (_serverSideConnection) {
            _serverSideConnection.Remove(connectionId);
            return _serverSideConnection.Count;
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
    internal void SetSubscriptions(Guid connectionId, EventSubscription[] subscriptions) {
        lock (_serverSideConnection) {
            if (_serverSideConnection.TryGetValue(connectionId, out var connection)) {
                connection.Subscriptions.Clear();
                connection.Subscriptions.AddRange(subscriptions);
            }
        }
    }
}


