namespace Relatude.DB.NodeServer.EventHub;
public class EventTriggerEngine : IDisposable {
    Timer _pulseTimer;
    public EventTriggerEngine() {
        _pulseTimer = new Timer(_ => pulse(), null, 1000, 1000);
    }
    void pulse() {
        // Console.WriteLine("EventTriggerEngine Pulse"); 
    }
    public void Dispose() {
        _pulseTimer?.Dispose();
    }
}
