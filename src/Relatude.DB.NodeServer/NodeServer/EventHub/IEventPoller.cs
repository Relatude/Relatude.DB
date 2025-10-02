namespace Relatude.DB.NodeServer.EventHub;
public class PollerAndDueTime(IEventPoller poller, DateTime dueTime) {
    public IEventPoller Poller { get; } = poller;
    public DateTime DueTime { get; set; } = dueTime;
}
public interface IEventPoller {
    string EventName { get; }
    EventDataFactory[]? Poll(RelatudeDBServer server, string?[] filters, bool onlyOnChange, out int msNextCollect);
}