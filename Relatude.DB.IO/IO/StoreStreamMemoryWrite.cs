using Relatude.DB.Common;

namespace Relatude.DB.IO;
public class StoreStreamMemoryWrite : IAppendStream {
    protected MemoryStream _ms;
    Action<MemoryStream> _onDispose;
    bool _isDisposed;
    ChecksumUtil _checkSum = new();
    public StoreStreamMemoryWrite(string name, MemoryStream ms, Action<MemoryStream> onDispose) {
        _ms = ms;
        _ms.Position = _ms.Length;
        _onDispose = onDispose;
        FileKey = name;
    }
    public void Append(byte[] data) {
        if (_isDisposed) throw new ObjectDisposedException("Stream is disposed. ");
        _ms.Write(data, 0, data.Length);
        _checkSum.EvaluateChecksumIfRecording(data);
    }
    public void Flush() { }
    public long Length { get => _ms.Length; }

    public string FileKey { get; }

    public void Get(long position, int count, byte[] buffer) {
        if (_isDisposed) throw new ObjectDisposedException("Stream is disposed. ");
        if (count > Length - position) count = (int)(Length - position);
        long position1 = this._ms.Position;
        _ms.Position = position;
        _ms.Read(buffer, 0, count);
        _ms.Position = position1;        
    }
    public void RecordChecksum() => _checkSum.RecordChecksum();
    public void WriteChecksum() => _checkSum.WriteChecksum(this);
    public void Dispose() {
        if (_isDisposed) return;
        _isDisposed = true;
        _onDispose(_ms);
    }
}