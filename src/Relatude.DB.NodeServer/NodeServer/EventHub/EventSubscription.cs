namespace Relatude.DB.NodeServer.EventHub;

public class EventSubscription{
    public Guid SubscriptionId { get; init; } = Guid.NewGuid();
    public HashSet<string> EventNames { get; } = [];
    public LinkedList<IEventData> EventQueue { get; } = [];
}
