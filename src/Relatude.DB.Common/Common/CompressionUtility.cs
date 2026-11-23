using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace Relatude.DB.Common;
public class CompressionUtility {
    /// <summary>
    /// Compresses the input byte array using GZip compression and returns the compressed data as a new byte array.
    /// </summary>
    public static byte[] Compress(byte[] data) => Compress(data.AsSpan());
    /// <summary>
    /// Compresses the input using GZip compression and returns the compressed data as a new byte array.
    /// </summary>
    public static byte[] Compress(ReadOnlySpan<byte> data) {
        var buffer = ArrayPool<byte>.Shared.Rent((int)GZipEncoder.GetMaxCompressedLength(data.Length));
        try {
            if (!GZipEncoder.TryCompress(data, buffer, out var written)) throw new InvalidOperationException("GZip output exceeded its maximum length.");
            return buffer.AsSpan(0, written).ToArray();
        } finally {
            ArrayPool<byte>.Shared.Return(buffer);
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
        // gzip ends with the uncompressed length, so the result is sized up front; deflate's 1032:1 limit bounds a corrupt one
        if (compressedData.Length >= 18) {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(compressedData.AsSpan(^4));
            if (length > 0 && length <= Array.MaxLength && length <= compressedData.Length * 1032L) {
                var result = GC.AllocateUninitializedArray<byte>((int)length);
                if (GZipDecoder.TryDecompress(compressedData, result, out var written) && written == length) return result;
            }
        }
        using (MemoryStream compressedStream = new MemoryStream(compressedData))
        using (GZipStream zipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
        using (MemoryStream resultStream = new MemoryStream()) {
            zipStream.CopyTo(resultStream);
            return resultStream.ToArray();
        }
    }
}
