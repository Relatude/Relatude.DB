namespace Relatude.DB.IO;

/// <summary>
/// A stream that bridges a push producer (writes via <see cref="Write"/>) to a pull consumer (reads via <see cref="Read"/>).
/// Call <see cref="Complete"/> when the producer is finished. Thread-safe for a single producer and single consumer.
/// </summary>
public class WriteToReadStream : Stream {
    readonly Queue<byte[]> _queue = new();
    readonly SemaphoreSlim _signal = new(0);
    bool _completed;
    volatile bool _disposed;
    Exception? _error;
    byte[]? _current;
    int _currentPos;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) {
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        lock (_queue) _queue.Enqueue(copy);
        if (!_disposed) _signal.Release();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override void Write(ReadOnlySpan<byte> buffer) {
        lock (_queue) _queue.Enqueue(buffer.ToArray());
        if (!_disposed) _signal.Release();
    }

    public override int Read(byte[] buffer, int offset, int count) {
        while (true) {
            if (_current != null) {
                var toCopy = Math.Min(count, _current.Length - _currentPos);
                Buffer.BlockCopy(_current, _currentPos, buffer, offset, toCopy);
                _currentPos += toCopy;
                if (_currentPos >= _current.Length) { _current = null; _currentPos = 0; }
                return toCopy;
            }
            if (_disposed) return 0;
            try { _signal.Wait(); } catch (ObjectDisposedException) { return 0; }
            lock (_queue) { if (_queue.Count > 0) { _current = _queue.Dequeue(); _currentPos = 0; continue; } }
            if (!_disposed) _signal.Release(); // re-signal so subsequent reads also see EOF
            if (_error != null) throw new IOException("Producer stream faulted.", _error);
            return 0;
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
        while (true) {
            if (_current != null) {
                var toCopy = Math.Min(count, _current.Length - _currentPos);
                Buffer.BlockCopy(_current, _currentPos, buffer, offset, toCopy);
                _currentPos += toCopy;
                if (_currentPos >= _current.Length) { _current = null; _currentPos = 0; }
                return toCopy;
            }
            if (_disposed) return 0;
            try { await _signal.WaitAsync(cancellationToken); } catch (ObjectDisposedException) { return 0; }
            lock (_queue) { if (_queue.Count > 0) { _current = _queue.Dequeue(); _currentPos = 0; continue; } }
            if (!_disposed) _signal.Release();
            if (_error != null) throw new IOException("Producer stream faulted.", _error);
            return 0;
        }
    }

    /// <summary>Signals the consumer that no more data will be written.</summary>
    public void Complete(Exception? error = null) {
        lock (_queue) { _completed = true; _error = error; }
        if (!_disposed) _signal.Release();
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _disposed = true; // set before Complete so Complete's Release is skipped
            if (!_completed) Complete();
            _signal.Dispose();
        }
        base.Dispose(disposing);
    }
}
