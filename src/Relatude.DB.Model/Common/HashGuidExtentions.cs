using System.Security.Cryptography;
using System.Text;

namespace Relatude.DB.Common;

public static class HashGuidExtentions {
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
}
