namespace Relatude.DB.IO;
/// <summary>
/// Not thread safe read stream interface.
/// </summary>
public interface IReadStream : IStream {
    bool More();
    byte[] Read(int length);
    void Skip(long length);
    long Position { get; set; }
    void RecordChecksum();
    void ValidateChecksum();
}
public static class IReadStreamExtensions {
    /// <summary>
    /// Moves the stream to the next valid marker. If the marker is not found it returns false.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="guid"></param>
    /// <returns></returns>
    public static bool MoveToNextValidMarker(this IReadStream stream, Guid guid) {
        var byteSequence = guid.ToByteArray();
        if (stream.Position + byteSequence.Length > stream.Length) return false;
        var buffer = stream.Read(byteSequence.Length);
        // this routine could be optimzed by using a circular buffer,
        // but it is not worth the effort as in most cases the marker is found in the first iteration.
        while (!buffer.SequenceEqual(byteSequence)) {
            if (stream.Position + buffer.Length > stream.Length) return false;
            Array.Copy(buffer, 1, buffer, 0, buffer.Length - 1);
            buffer[buffer.Length - 1] = stream.ReadOneByte();
        }
        return true;
    }
    /// <summary>
    /// Reads the next bytes and valides it to the marker. It throws an exception if the marker does not match.
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="validMarker"></param>
    /// <exception cref="Exception"></exception>
    public static void ValidateMarker(this IReadStream stream, Guid validMarker) {
        if (stream.ReadGuid() != validMarker) throw new Exception("Invalid binary data. ");
    }
    public static void WriteMarker(this IAppendStream s, Guid v) {
        s.Append(v.ToByteArray());
    }




}
