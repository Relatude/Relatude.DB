using Relatude.DB.Common;
using Relatude.DB.FileConversion;
using System.IO.Compression;
using System.Security.Cryptography;
namespace Relatude.DB.Web;

public class BinaryUrlFileAdjustmentEncoder(Guid secretHashKey) : IUrlFileAdjustmentEncoder {
    const bool UseCompression = true;
    const int SignatureBytes = 8;
    static CompressionLevel compressionLevel = CompressionLevel.SmallestSize;
    readonly byte[] _hmacKey = secretHashKey.ToByteArray();
    Cache<string, FileAdjustment> _cache1 = new(1000);
    Cache<Guid, string> _cache2 = new(1000);

    byte[] Sign(byte[] data) {
        var hash = HMACSHA256.HashData(_hmacKey, data);
        return hash[..SignatureBytes];
    }
    byte[] AppendSignature(byte[] data) {
        var sig = Sign(data);
        var result = new byte[data.Length + SignatureBytes];
        data.CopyTo(result, 0);
        sig.CopyTo(result, data.Length);
        return result;
    }
    byte[] VerifyAndStrip(byte[] data) {
        if (data.Length < SignatureBytes) throw new ArgumentException("Payload too short.");
        var payload = data[..^SignatureBytes];
        var expected = Sign(payload);
        var actual = data[^SignatureBytes..];
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new ArgumentException("Invalid signature — adjustment has been tampered with or was produced without the correct secret key.");
        return payload;
    }

    public FileAdjustment GetAdjustmentFromEncodedString(string urlString) {
        if (_cache1.TryGet(urlString, out var cachedAdj)) return cachedAdj;
        if (B64.TryDecodeFromUrlParameter(urlString, out var bytes)) {
            bytes = VerifyAndStrip(bytes);
            if (UseCompression) {
                using var input = new MemoryStream(bytes);
                using var brotli = new BrotliStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                brotli.CopyTo(output);
                bytes = output.ToArray();
            }
            var adjustment = FileAdjustment.FromBytes(bytes);
            adjustment.BasicSanitization(); // to prevent "crazy" values, causing issues in downstream processing (e.g. negative or extreme dimensions, memory exhaustion, etc.)
            _cache1.Set(urlString, adjustment, 1);
            return adjustment;
        }
        throw new ArgumentException("Invalid encoded string.", nameof(urlString));
    }
    public string GetEncodedString(FileAdjustment adj) {
        var key = adj.GetKey();
        if (_cache2.TryGet(key, out var cachedString)) return cachedString;
        var bytes = adj.ToBytes();
        if (UseCompression) {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, compressionLevel)) brotli.Write(bytes);
            bytes = output.ToArray();
        }
        var encodedString = B64.EncodeForUrl(AppendSignature(bytes));
        _cache2.Set(key, encodedString, 1);
        return encodedString;
    }
}

