namespace Relatude.DB.IO;
/// <summary>
/// Reads a file through the append stream that has it open, up to the length it had when this was made. The
/// file stays open for appending - and so held against other processes - while it is read: a read stream of
/// its own could only be opened once the append stream had let go of the file. Disposing this leaves the
/// append stream open.
/// </summary>
public sealed class StoreStreamAppendRead : IReadStream {
    readonly IAppendStream _source;
    readonly byte[] _buffer;
    long _bufferStart;
    int _bufferLength;
    long _bytesRead;
    readonly ChecksumUtil _checkSum = new();
    public StoreStreamAppendRead(IAppendStream source, long position, int bufferSize = 4 * 1024 * 1024) {
        _source = source;
        Length = source.Length;
        Position = position;
        _buffer = new byte[bufferSize];
    }
    public string FileKey => _source.FileKey;
    public long Length { get; }
    public long Position { get; set; }
    public bool More() => Position < Length;
    public byte[] Read(int length) {
        var result = new byte[Math.Max(0, Math.Min(Length - Position, length))];
        copyTo(result);
        _checkSum.EvaluateChecksumIfRecording(result);
        return result;
    }
    public int ReadInto(Span<byte> buffer) {
        var read = buffer[..(int)Math.Max(0, Math.Min(Length - Position, buffer.Length))];
        copyTo(read);
        _checkSum.EvaluateChecksumIfRecording(read);
        return read.Length;
    }
    public Task<int> ReadAsync(byte[] buffer, int count) => Task.FromResult(ReadInto(buffer.AsSpan(0, count)));
    void copyTo(Span<byte> destination) {
        var done = 0;
        while (done < destination.Length) {
            if (Position < _bufferStart || Position >= _bufferStart + _bufferLength) {
                _bufferStart = Position;
                _bufferLength = (int)Math.Min(_buffer.Length, Length - Position);
                _source.Get(_bufferStart, _bufferLength, _buffer);
            }
            var offset = (int)(Position - _bufferStart);
            var n = Math.Min(destination.Length - done, _bufferLength - offset);
            _buffer.AsSpan(offset, n).CopyTo(destination[done..]);
            done += n;
            Position += n;
        }
        _bytesRead += done;
    }
    public void Skip(long length) => Position += length;
    public void RecordChecksum() => _checkSum.RecordChecksum();
    public void ValidateChecksum() => _checkSum.ValidateChecksum(this);
    public void ResetByteCounter() => _bytesRead = 0;
    public long GetBytesRead() => _bytesRead;
    public void Dispose() { }
}
