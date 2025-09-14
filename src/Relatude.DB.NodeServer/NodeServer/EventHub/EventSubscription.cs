namespace Relatude.DB.NodeServer.EventHub;
public class EventSubscription() {
    public Guid SubscriptionId { get; } = Guid.NewGuid();
    public HashSet<string> EventNames { get; } = [];
    public LinkedList<EventData> EventQueue { get; } = new();
}
