namespace WAF.DataStores.Files;
public sealed class AsyncReaderWriterLock : IDisposable
{
    readonly SemaphoreSlim _readSemaphore = new(1, 1);
    readonly SemaphoreSlim _writeSemaphore = new(1, 1);
    int _readerCount;
    public async Task AcquireWriterLock(CancellationToken token = default)
    {
        await _writeSemaphore.WaitAsync(token).ConfigureAwait(false);
        await SafeAcquireReadSemaphore(token).ConfigureAwait(false);
    }
    public void ReleaseWriterLock()
    {
        _readSemaphore.Release();
        _writeSemaphore.Release();
    }
    public async Task AcquireReaderLock(CancellationToken token = default)
    {
        await _writeSemaphore.WaitAsync(token).ConfigureAwait(false);
        if (Interlocked.Increment(ref _readerCount) == 1)
        {
            try
            {
                await SafeAcquireReadSemaphore(token).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Decrement(ref _readerCount);
                throw;
            }
        }
        _writeSemaphore.Release();
    }
    public void ReleaseReaderLock()
    {
        if (Interlocked.Decrement(ref _readerCount) == 0)
        {
            _readSemaphore.Release();
        }
    }
    private async Task SafeAcquireReadSemaphore(CancellationToken token)
    {
        try
        {
            await _readSemaphore.WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            _writeSemaphore.Release();
            throw;
        }
    }
    public void Dispose()
    {
        _writeSemaphore.Dispose();
        _readSemaphore.Dispose();
    }
}