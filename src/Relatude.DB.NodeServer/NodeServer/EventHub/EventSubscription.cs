namespace Relatude.DB.NodeServer.EventHub;

public interface IEventSubscription {
    public Guid SubscriptionId { get; init; }
    public HashSet<string> EventNames { get; init; }
    public LinkedList<IEventData> EventQueue { get; }
    public object? DataGeneric { get; init; }
}

public class EventSubscription<T> : IEventSubscription {
    public Guid SubscriptionId { get; init; }
    public required HashSet<string> EventNames { get; init; }
    public LinkedList<IEventData> EventQueue { get; } = new();
    public object? DataGeneric { get; init; }
    public T Data => (T)DataGeneric!;
}
