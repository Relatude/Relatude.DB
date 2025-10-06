namespace Relatude.DB.NodeServer.EventHub;
public class TriggerAndDueTime(IEventTrigger trigger, DateTime dueTime) {
    public IEventTrigger Trigger { get; } = trigger;
    public DateTime DueTime { get; set; } = dueTime;
}
public interface IEventTrigger {
    string EventName { get; }
    EventPayload[]? CollectPayloads(RelatudeDBServer server, string?[] filters, out int msNextCollect);
}