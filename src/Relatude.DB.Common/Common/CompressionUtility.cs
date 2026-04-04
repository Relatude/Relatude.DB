using System.IO.Compression;

namespace Relatude.DB.Common;
public class CompressionUtility {
    /// <summary>
    /// Compresses the input byte array using GZip compression and returns the compressed data as a new byte array.
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    public static byte[] Compress(byte[] data) {
        using (MemoryStream compressedStream = new MemoryStream()) {
            using (GZipStream zipStream = new GZipStream(compressedStream, CompressionMode.Compress)) {
                zipStream.Write(data, 0, data.Length);
            }
            return compressedStream.ToArray();
        }
    }
    /// <summary>
    /// Decompresses a byte array that was compressed using the GZip algorithm.
    /// </summary>
    /// <remarks>The method expects the input data to be in the GZip compression format. If the data is not in
    /// the correct format or is corrupted, a decompression-related exception may be thrown.</remarks>
    /// <param name="compressedData">The compressed data as a byte array. Must not be null and must contain data in GZip format.</param>
    /// <returns>A byte array containing the decompressed data.</returns>
    public static byte[] Decompress(byte[] compressedData) {
        using (MemoryStream compressedStream = new MemoryStream(compressedData))
        using (GZipStream zipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
        using (MemoryStream resultStream = new MemoryStream()) {
            zipStream.CopyTo(resultStream);
            return resultStream.ToArray();
        }
    }
}