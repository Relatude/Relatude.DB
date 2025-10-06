namespace Relatude.DB.NodeServer.EventHub;
public class EventContext {
    public RelatudeDBServer Server => RelatudeDBRuntime.Server;
    //public string UserIdentity => "Anonymous"; // TODO later, if needed
    public EventContext() {
    }
}
