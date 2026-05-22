namespace Relatude.DB.FileConversion;

internal class FileConversionScheduler(Action task, Action<Exception> onError) {
    object _pulseStateLock = new();
    bool _pulsesRunning = false;
    Timer? _hart;
    public void Start() {
        lock (_pulseStateLock) {
            _hart = new Timer(_ => pulse(), null, 1000, 1000);
        }
    }
    public bool IsStopped {
        get {
            lock (_pulseStateLock) {
                return _hart == null;
            }
        }
    }
    public void RunSoon() {
        lock (_pulseStateLock) {
            if (_hart == null) return;
            _hart.Change(1, 10000);
        }
    }
    public void Stop() {
        lock (_pulseStateLock) {
            _hart?.Dispose();
            _hart = null;
        }
    }
    void pulse() {
        lock (_pulseStateLock) {
            if (_pulsesRunning) return;
            _pulsesRunning = true;
        }
        try {
            task();
        } catch (Exception ex) {
            onError(ex);
        } finally {
            lock (_pulseStateLock) {
                _pulsesRunning = false;
            }
        }
    }
}