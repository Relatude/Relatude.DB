using System.Runtime.CompilerServices;

namespace Relatude.DB.IO;

public readonly record struct IOCounters(long Reads, long Writes, long BytesRead, long BytesWritten) {
    public static IOCounters operator +(IOCounters a, IOCounters b) => new(a.Reads + b.Reads, a.Writes + b.Writes, a.BytesRead + b.BytesRead, a.BytesWritten + b.BytesWritten);
}

public sealed class IOCounter {
    long _reads;
    long _writes;
    long _bytesRead;
    long _bytesWritten;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Read(long bytes) {
        _reads++;
        _bytesRead += bytes;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(long bytes) {
        _writes++;
        _bytesWritten += bytes;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReadThreadSafe(long bytes) {
        Interlocked.Increment(ref _reads);
        Interlocked.Add(ref _bytesRead, bytes);
    }
    public IOCounters Snapshot => new(Interlocked.Read(ref _reads), Interlocked.Read(ref _writes), Interlocked.Read(ref _bytesRead), Interlocked.Read(ref _bytesWritten));
}
