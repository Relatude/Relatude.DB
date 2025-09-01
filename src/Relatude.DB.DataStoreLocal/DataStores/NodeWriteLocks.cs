using System.Diagnostics;
namespace Relatude.DB.DataStores;
/// <summary>
///  This class is used to manage long term write locks on nodes. 
///  The already existing ReadWriteSlimLocks only spans the duration of a single execution.
///  Lock of id 0 is a global lock, and will prevent all other locks from being obtained.
/// </summary>
internal class NodeWriteLocks {
    readonly Dictionary<int, Guid> _locksByNodeId = [];
    readonly Dictionary<Guid, lockRecord> _locksById = [];
    private class lockRecord {
        public int NodeId;
        public DateTime LastRefresh;
        public TimeSpan Duration;
        public bool IsActive() => DateTime.UtcNow.Subtract(LastRefresh) <= Duration;
    }
    public bool IsLocked(int nodeId, HashSet<Guid>? lockExcemptions) {
        removeExpired();
        if (_locksByNodeId.TryGetValue(0, out var globalLockId)) { // global lock
            if (lockExcemptions == null || !lockExcemptions.Contains(globalLockId)) return true;
        }
        if (_locksByNodeId.TryGetValue(nodeId, out var lockId)) {
            return (lockExcemptions == null || !lockExcemptions.Contains(lockId));
        }
        return false;
    }
    public async Task<Guid> RequestLockAsync(int nodeId, double lockDurationInMs, double maxWaitLimitInMs) {
        if (tryRequestLockAsync(nodeId, lockDurationInMs, out var lockId)) return lockId;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxWaitLimitInMs) {
            await Task.Delay(sw.ElapsedMilliseconds < 100 ? 10 : 200); // step up wait before next try after 100ms
            if (tryRequestLockAsync(nodeId, lockDurationInMs, out lockId)) return lockId;
        }
        throw new Exception("Unable to obtain lock within wait limit. ");
    }
    bool tryRequestLockAsync(int nodeId, double lockDurationInMs, out Guid newLockId) {
        removeExpired();
        newLockId = Guid.Empty;
        if (_locksByNodeId.ContainsKey(0)) return false; // global lock
        if (_locksByNodeId.ContainsKey(nodeId)) return false;
        newLockId = Guid.NewGuid();
        _locksByNodeId.Add(nodeId, newLockId);
        _locksById.Add(newLockId, new() {
            NodeId = nodeId,
            LastRefresh = DateTime.UtcNow,
            Duration = TimeSpan.FromMilliseconds(lockDurationInMs)
        });
        return true;
    }
    public void RefreshLock(Guid lockId) {
        removeExpired();
        if (_locksById.TryGetValue(lockId, out var record)) {
            record.LastRefresh = DateTime.UtcNow;
        } else {
            throw new Exception("Unable to refresh lock. It is unknown. ");
        }
    }
    public void Unlock(Guid lockId) {
        if (_locksById.TryGetValue(lockId, out var record)) {
            _locksById.Remove(lockId);
            _locksByNodeId.Remove(record.NodeId);
        } else {
            // ignore unknown lock, it is legal to unlock a lock that is already removed
        }
    }
    public bool AnyLocks() {
        removeExpired();
        return _locksById.Count > 0;
    }
    internal bool LocksAreActive(IEnumerable<Guid> guids) => guids.All(LockIsActive);
    internal bool LockIsActive(Guid lockId) {
        return _locksById.TryGetValue(lockId, out var record) && record.IsActive();
    }
    void removeExpired() {
        if (_locksById.Count == 0) return;
        var expired = _locksById.Where(kv => !kv.Value.IsActive()).ToList();
        if (expired.Count == _locksById.Count) {
            _locksByNodeId.Clear();
            _locksById.Clear();
            return;
        }
        foreach (var kv in expired) {
            _locksById.Remove(kv.Key);
            _locksByNodeId.Remove(kv.Value.NodeId);
        }
    }
}
