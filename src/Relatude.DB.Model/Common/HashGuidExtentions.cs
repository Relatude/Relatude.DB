using System.Security.Cryptography;
using System.Text;

namespace Relatude.DB.Common;

public static class HashGuidExtentions {
    public static int GenerateHashInt(this string value) {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToInt32(hash, 0);
    }
    public static uint GenerateHashUInt(this string value) {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToUInt32(hash, 0);
    }
    public static long GenerateHashLong(this string value) {
        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToInt64(hash, 0);
    }
    /// <summary>
    /// Generates a deterministic Guid based on the input string using SHA-1 hashing.
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    public static Guid GenerateHashGuid(this string value) {
        byte[] stringbytes = Encoding.UTF8.GetBytes(value);
        byte[] hashedBytes = SHA1.Create().ComputeHash(stringbytes);
        Array.Resize(ref hashedBytes, 16);
        return new Guid(hashedBytes);
    }
    public static Guid CombineHashGuid(this Guid value, Guid other) {
        byte[] valueBytes = value.ToByteArray();
        byte[] otherBytes = other.ToByteArray();
        byte[] combinedBytes = new byte[valueBytes.Length + otherBytes.Length];
        Buffer.BlockCopy(valueBytes, 0, combinedBytes, 0, valueBytes.Length);
        Buffer.BlockCopy(otherBytes, 0, combinedBytes, valueBytes.Length, otherBytes.Length);
        byte[] hashedBytes = SHA1.Create().ComputeHash(combinedBytes);
        Array.Resize(ref hashedBytes, 16);
        return new Guid(hashedBytes);
    }

    public static string GetShortHashForUrl(this string url, Guid seed) {
        Span<byte> key = stackalloc byte[16];
        seed.TryWriteBytes(key);
        int maxLen = Encoding.UTF8.GetMaxByteCount(url.Length);
        Span<byte> src = maxLen <= 512 ? stackalloc byte[maxLen] : new byte[maxLen];
        int written = Encoding.UTF8.GetBytes(url, src);
        Span<byte> hash = stackalloc byte[32];
        HMACSHA256.HashData(key, src[..written], hash);
        // 6 bytes → exactly 8 base64 chars, no padding (6 % 3 == 0)
        Span<char> buf = stackalloc char[8];
        Convert.TryToBase64Chars(hash[..6], buf, out _);
        for (int i = 0; i < 8; i++) { if (buf[i] == '+') buf[i] = '-'; else if (buf[i] == '/') buf[i] = '_'; }
        return new string(buf);
    }

}
