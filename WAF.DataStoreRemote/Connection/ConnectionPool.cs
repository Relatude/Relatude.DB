using System.Diagnostics;

namespace WAF.Connection {
    internal class ConnectionPool : IDisposable {
        int _maxConnections;
        readonly Func<ISingleThreadedConnection> _create;
        HashSet<ISingleThreadedConnection> _busy = new();
        Stack<ISingleThreadedConnection> _idle = new();
        readonly EventWaitHandle _releaseEvent;
        readonly Timer _discardTimer;
        readonly RemoteConfiguration _config;
        object _lock = new();
        public ConnectionPool(Func<ISingleThreadedConnection> create, RemoteConfiguration config) {
            _create = create;
            _releaseEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
            _maxConnections = config.MaxNoConnections;
            if (_maxConnections < 1) _maxConnections = 30;
            var cleanupIntervalSec = 30;
            if (cleanupIntervalSec > config.IdleTimeoutInPoolInSec) cleanupIntervalSec = config.IdleTimeoutInPoolInSec;
            if (cleanupIntervalSec > config.ExecutionTimeoutInSec) cleanupIntervalSec = config.ExecutionTimeoutInSec;
            _discardTimer = new Timer(disposeUnusedAndStalled, null, cleanupIntervalSec * 1000, cleanupIntervalSec * 1000);
            _config = config;
        }
        public ReservedConnection ReserveConnection() {
            Stopwatch s = new();
            s.Start();            
            while (true) {
                lock (_lock) {
                    if (_idle.TryPop(out var cn)) { // are any idle connections ready?
                        _busy.Add(cn);
                        return new ReservedConnection(cn);
                    } else if (_idle.Count + _busy.Count < _maxConnections) { // create new if not exceeding max
                        var newCn = _create();
                        _busy.Add(newCn);
                        return new ReservedConnection(newCn);
                    }
                }
                _releaseEvent.WaitOne(100); // waiting for an idle connection, wait up to 100ms, then check total time waited
                if (s.Elapsed.TotalSeconds > _config.ConnectionTimeoutInSec) throw new TimeoutException("Unable to aquire a connection. Too many connections active. ");
            }
        }
        public void ReleaseConnection(ReservedConnection cn) {
            cn.AllowUse = false; // prohibits further use in case outer class referring to it use it after it is released
            lock (_lock) {
                _busy.Remove(cn._innerConnection);
                _idle.Push(cn._innerConnection);
            }
            _releaseEvent.Set();
        }
        void disposeUnusedAndStalled(object? state) {
            lock (_lock) {
                Stack<ISingleThreadedConnection> keepAsIdle = new();
                HashSet<ISingleThreadedConnection> keepAsBusy = new();
                List<ISingleThreadedConnection> discard = new();
                foreach (var cn in _idle) {
                    if (cn.MsSinceLastUsed() / 1000 > _config.IdleTimeoutInPoolInSec) {
                        discard.Add(cn);
                    } else {
                        keepAsIdle.Push(cn);
                    }
                }
                foreach (var cn in _busy) {
                    if (cn.CurrentCallDurationInMs() / 1000 > _config.ExecutionTimeoutInSec) {
                        discard.Add(cn); // stalled...
                        cn.FlaggedAsStalled = true; // will cause error in ongoing calls
                    } else {
                        keepAsBusy.Add(cn);
                    }
                }
                if (discard.Count > 0) {
                    _idle = keepAsIdle;
                    _busy = keepAsBusy;
                    try {
                        foreach (var cn in discard) cn.Dispose();
                    } catch { }
                }
            }
        }
        public void Dispose() {
            _discardTimer.Dispose();
            foreach (var cn in _busy) cn.Dispose();
            foreach (var cn in _idle) cn.Dispose();
        }
    }
}
