namespace Relatude.DB.IO;
public class ReadStreamWrapper : System.IO.Stream {
    private readonly IReadStream _stream;
    public ReadStreamWrapper(IReadStream stream) {
        _stream = stream;
    }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _stream.Length;
    public override long Position { get => _stream.Position; set => _stream.Position = value; }

    public override void Flush() {
        
    }
    public override int Read(byte[] buffer, int offset, int count) {
        var data = _stream.Read(count);
        Array.Copy(data, 0, buffer, offset, data.Length);
        return data.Length;
    }

    public override long Seek(long offset, SeekOrigin origin) {
        int newPosition;
        switch (origin) {
            case SeekOrigin.Begin:
                newPosition = (int)offset;
                break;
            case SeekOrigin.Current:
                newPosition = (int)(_stream.Position + offset);
                break;
            case SeekOrigin.End:
                newPosition = (int)(_stream.Length + offset);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(origin), origin, null);
        }
        _stream.Position = newPosition;
        return _stream.Position;
    }
    public override void SetLength(long value) {
        throw new NotImplementedException();
    }
    public override void Write(byte[] buffer, int offset, int count) {
        throw new NotImplementedException();
    }
    protected override void Dispose(bool disposing) {
        _stream.Dispose();
        base.Dispose(disposing);
    }
    public override int ReadTimeout { get => 20000; set => base.ReadTimeout = value; }
    public override int WriteTimeout { get => 20000; set => base.WriteTimeout = value; }
}
