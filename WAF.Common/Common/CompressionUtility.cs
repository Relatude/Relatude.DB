using System.IO.Compression;

namespace WAF.Common;
public class CompressionUtility {
    public static byte[] Compress(byte[] data) {
        using (MemoryStream compressedStream = new MemoryStream()) {
            using (GZipStream zipStream = new GZipStream(compressedStream, CompressionMode.Compress)) {
                zipStream.Write(data, 0, data.Length);
            }
            return compressedStream.ToArray();
        }
    }

    public static byte[] Decompress(byte[] compressedData) {
        using (MemoryStream compressedStream = new MemoryStream(compressedData))
        using (GZipStream zipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
        using (MemoryStream resultStream = new MemoryStream()) {
            zipStream.CopyTo(resultStream);
            return resultStream.ToArray();
        }
    }
}