namespace Relatude.DB.IO;
public class WriteStreamWrapper : System.IO.Stream {
    private readonly IAppendStream _stream;
    public WriteStreamWrapper(IAppendStream stream) {
        _stream = stream;
    }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _stream.Length;
    public override long Position { get => _stream.Length; set => throw new NotImplementedException(); }
    public override void Flush() {
        throw new NotImplementedException();
    }
    public override int Read(byte[] buffer, int offset, int count) {
        throw new NotImplementedException();
    }
    public override long Seek(long offset, SeekOrigin origin) {
        throw new NotImplementedException();
    }
    public override void SetLength(long value) {
        throw new NotImplementedException();
    }
    public override void Write(byte[] buffer, int offset, int count) {
        _stream.Append(buffer[offset..(offset + count)]);
    }
    protected override void Dispose(bool disposing) {
        _stream.Dispose();
        base.Dispose(disposing);
    }
}