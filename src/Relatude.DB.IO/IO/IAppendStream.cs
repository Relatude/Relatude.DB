namespace Relatude.DB.IO;
// Thread-safe interface for appending data to a stream.
public interface IAppendStream : IStream{
    void Append(byte[] data);
    void RecordChecksum();
    void WriteChecksum();
    void Flush(bool deepFlush);
    void Get(long position, int count, byte[] buffer);
    void ResetByteCounter();
    long GetBytesRead();
    long GetBytesWritten();
}
