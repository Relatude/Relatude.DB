using Relatude.DB.Common;
using Relatude.DB.Datamodels;
namespace Relatude.DB.IO;

// Parses primitives directly from a whole-file byte[] in place: no per-field allocation and no
// virtual stream call, so the hot state-load loops run at memory speed. Mirrors the IReadStream
// primitive/marker/checksum API exactly (same per-field chunking) so on-disk checksums still match.
public sealed class BufferReader {
    readonly byte[] _data;
    int _pos;
    uint _checksum;
    bool _recording;
    public BufferReader(byte[] data, int position = 0) { _data = data; _pos = position; }
    public long Length => _data.Length;
    public long Position { get => _pos; set => _pos = (int)value; }
    public bool More() => _pos < _data.Length;
    public void Skip(long length) => _pos += (int)length;

    ReadOnlySpan<byte> take(int n) {
        ReadOnlySpan<byte> s = _data.AsSpan(_pos, n);
        _pos += n;
        if (_recording) s.EvaluateChecksum(ref _checksum);
        return s;
    }

    public byte ReadOneByte() => take(1)[0];
    public bool ReadBool() => take(1)[0] == 1;
    public int ReadInt() => BitConverter.ToInt32(take(4));
    public uint ReadUInt() => BitConverter.ToUInt32(take(4));
    public long ReadLong() => BitConverter.ToInt64(take(8));
    public Guid ReadGuid() => new Guid(take(16));
    public DateTime ReadDateTimeUtc() => new DateTime(BitConverter.ToInt64(take(8)), DateTimeKind.Utc);
    public int ReadVerifiedInt() {
        var v1 = BitConverter.ToInt32(take(4));
        var v2 = BitConverter.ToInt32(take(4));
        if (v1 != -v2) throw new Exception("Verification failed. Invalid binary data. ");
        return v1;
    }
    public long ReadVerifiedLong() {
        var v1 = BitConverter.ToInt64(take(8));
        var v2 = BitConverter.ToInt64(take(8));
        if (v1 != -v2) throw new Exception("Verification failed. Invalid binary data. ");
        return v1;
    }
    public byte[] ReadByteArray() => take(ReadVerifiedInt()).ToArray();
    public string ReadString() => RelatudeDBGlobals.Encoding.GetString(take(ReadVerifiedInt()));
    public int[] ReadIntArray() {
        var len = ReadVerifiedInt();
        var data = take(len * 4);
        var r = new int[len];
        for (var n = 0; n < len; n++) r[n] = BitConverter.ToInt32(data.Slice(n * 4, 4));
        return r;
    }

    public void RecordChecksum() { _checksum = 0; _recording = true; }
    public void ValidateChecksum() {
        _recording = false;
        if (ReadUInt() != _checksum) throw new Exception("Invalid checksum");
        _checksum = 0;
    }
    public void ValidateMarker(Guid marker) {
        if (ReadGuid() != marker) throw new Exception("Invalid binary data. ");
    }

    // Bridge for the few sub-readers still written against IReadStream; shares this reader's
    // position and checksum so reads interleave correctly.
    public IReadStream AsReadStream() => new BufferReaderStream(this);
    internal byte[] ReadBytes(int n) => take((int)Math.Min(n, _data.Length - _pos)).ToArray();
    internal int ReadIntoSpan(Span<byte> dest) {
        var n = (int)Math.Min(dest.Length, _data.Length - _pos);
        take(n).CopyTo(dest);
        return n;
    }
}

sealed class BufferReaderStream(BufferReader r) : IReadStream {
    public string FileKey => "state";
    public long Length => r.Length;
    public long Position { get => r.Position; set => r.Position = value; }
    public bool More() => r.More();
    public void Skip(long length) => r.Skip(length);
    public byte[] Read(int length) => r.ReadBytes(length);
    public int ReadInto(Span<byte> buffer) => r.ReadIntoSpan(buffer);
    public void RecordChecksum() => r.RecordChecksum();
    public void ValidateChecksum() => r.ValidateChecksum();
    public long GetBytesRead() => 0;
    public void ResetByteCounter() { }
    public Task<int> ReadAsync(byte[] buffer, int count) => Task.FromResult(r.ReadIntoSpan(buffer.AsSpan(0, count)));
    public void Dispose() { }
}
