using System.IO;
using WAF.Common;

namespace WAF.IO;
public class StoreStreamMemoryRead : IReadStream {
    long _position;
    byte[] _bytes;
    bool _isDisposed;
    Action _onDispose;
    ChecksumUtil _checkSum = new();
    public StoreStreamMemoryRead(byte[] bytes, long position, Action onDispose) {
        _bytes = bytes;
        _position = position;
        _onDispose = onDispose;
    }
    public long Position { get => _position; set => _position = value; }
    public bool More() => _position < _bytes.Length;
    public byte[] Read(int length) {
        if (_isDisposed) throw new ObjectDisposedException("Stream is disposed. ");
        length = (int)Math.Min(length, Length - Position);
        var block = new byte[length];
        Array.Copy(_bytes, _position, block, 0, length);
        _position += length;
        _checkSum.EvaluateChecksumIfRecording(block);
        return block;
    }
    public long Length => _bytes.Length;
    public void Skip(long length) => _position += length;
    public void RecordChecksum() => _checkSum.RecordChecksum();
    public void ValidateChecksum() => _checkSum.ValidateChecksum(this);
    public void Dispose() {
        if (_isDisposed) return;
        _isDisposed = true;
        _onDispose();
    }
}