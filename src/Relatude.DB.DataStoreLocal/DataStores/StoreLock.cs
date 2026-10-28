namespace Relatude.DB.DataStores;

/// <summary>
/// The store's lock: a <see cref="ReaderWriterLockSlim"/> taking recursion, except that a thread holding the write lock
/// does not take the read lock again, as it has the store to itself already. A transaction reads a great deal under its
/// write lock (the address step, plugins, relations), and each read lock it let go of woke every request waiting to
/// read, only for them to find the write lock still held and wait again: a kernel call each time, so a transaction ran
/// slower the more requests were waiting for it (9.6 s of a 13.3 s one, with requests waiting).
/// <para>A read lock taken under the write lock and still held when the write lock goes (a downgrade) is taken for
/// real before it goes, so the thread holds what it would have held.</para>
/// </summary>
internal sealed class StoreLock {
    readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
    readonly ThreadLocal<int> _readsUnderWrite = new(); // read locks this thread did not take, holding the write lock

    public int WaitingReadCount => _lock.WaitingReadCount;
    public int WaitingWriteCount => _lock.WaitingWriteCount;

    public void EnterReadLock() {
        if (_lock.IsWriteLockHeld) {
            _readsUnderWrite.Value++;
            return;
        }
        _lock.EnterReadLock();
    }
    public bool TryEnterReadLock(int millisecondsTimeout) {
        if (_lock.IsWriteLockHeld) {
            _readsUnderWrite.Value++;
            return true;
        }
        return _lock.TryEnterReadLock(millisecondsTimeout);
    }
    public void ExitReadLock() {
        if (_lock.IsWriteLockHeld) {
            var reads = _readsUnderWrite.Value;
            if (reads > 0) {
                _readsUnderWrite.Value = reads - 1;
                return;
            }
        }
        _lock.ExitReadLock();
    }
    public void EnterWriteLock() => _lock.EnterWriteLock();
    public bool TryEnterWriteLock(int millisecondsTimeout) => _lock.TryEnterWriteLock(millisecondsTimeout);
    public void ExitWriteLock() {
        if (_lock.RecursiveWriteCount == 1) {
            var reads = _readsUnderWrite.Value;
            if (reads > 0) {
                for (var i = 0; i < reads; i++) _lock.EnterReadLock(); // allowed: the write lock is still held
                _readsUnderWrite.Value = 0;
            }
        }
        _lock.ExitWriteLock();
    }
}
