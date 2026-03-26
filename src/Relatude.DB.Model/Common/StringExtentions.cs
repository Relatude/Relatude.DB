using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Relatude.DB.Common;

public static class StringExtentions {
    public static Guid GenerateGuid(this string value) {
        byte[] stringbytes = Encoding.UTF8.GetBytes(value);
        byte[] hashedBytes = SHA1.Create().ComputeHash(stringbytes);
        Array.Resize(ref hashedBytes, 16);
        return new Guid(hashedBytes);
    }
    public static Guid GenerateGuidSafe(this string? value) {
        if (value == null) return Guid.Empty;
        int byteCount = Encoding.UTF8.GetByteCount(value);
        byte[]? rented = null;
        Span<byte> utf8Bytes = byteCount <= 512
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));

        try {
            Encoding.UTF8.GetBytes(value, utf8Bytes);

            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(utf8Bytes, hash);

            Span<byte> guidBytes = stackalloc byte[16];
            hash[..16].CopyTo(guidBytes);

            guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x80);
            guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

            return new Guid(guidBytes);
        } finally {
            if (rented != null) ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
