namespace Relatude.DB.IO;

public class ReadStreamWrapper : System.IO.Stream {
    private readonly IReadStream _stream;
    public IReadStream InnerStream => _stream;
    public static Stream Wrap(IReadStream stream) {
        // If this is a disc stream - possibly behind the buffering wrapper - switch to a direct
        // FileStream. This avoids double buffering and gives random-access consumers (e.g. media
        // range requests / image decoding) an efficient, plainly seekable stream without the
        // SequentialScan hint that the bulk-read path uses.
        var disc = stream as StoreStreamDiscRead
                   ?? (stream as StoreStreamBufferedRead)?.InnerStream as StoreStreamDiscRead;
        if (disc != null) {
            var filePath = disc.InnerFilePath;
            stream.Dispose(); // disposing the buffered wrapper also disposes the inner disc stream
            return File.OpenRead(filePath);
        } else {
            return new ReadStreamWrapper(stream);
        }
    }
    ReadStreamWrapper(IReadStream stream) {
        _stream = stream;
    }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _stream.Length;
    public override long Position { get => _stream.Position; set => _stream.Position = value; }

    public override void Flush() {

    }
    // static long _totalBytesRead;
    public override int Read(byte[] buffer, int offset, int count) {
        var data = _stream.Read(count);
        Array.Copy(data, 0, buffer, offset, data.Length);
        //_totalBytesRead += data.Length;
        //var positionInPercentag = (int)Math.Round(_stream.Position / (double)_stream.Length * 100);
        //Console.WriteLine($"Position: {positionInPercentag}% , count: {count}, read: {data.Length} bytes, total read: {_totalBytesRead.InKB()}");
        return data.Length;
    }

    // without this the base class runs Read on a thread of the pool, which then waits for each download
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
        if (offset == 0) return await _stream.ReadAsync(buffer, count);
        var part = new byte[count];
        var read = await _stream.ReadAsync(part, count);
        Array.Copy(part, 0, buffer, offset, read);
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin) {
        _stream.Position = origin switch {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _stream.Position + offset,
            SeekOrigin.End => _stream.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null),
        };
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
