namespace WAF.IO;
// Thread-safe interface for appending data to a stream.
public interface IAppendStream : IStream{
    string FileKey { get; }
    void Append(byte[] data);
    void RecordChecksum();
    void WriteChecksum();
    void Flush();
    /// <summary>
    /// Gets data from stream, starting at position, and copying count bytes to buffer
    /// </summary>
    /// <param name="position">Absolute byte position in file</param>
    /// <param name="count">Bytes to read to buffer</param>
    /// <param name="buffer">Buffer to copy data to. Must be equal or bigger than count</param>
    /// <returns></returns>
    void Get(long position, int count, byte[] buffer);
    long Length { get; }
}
