using System.Security.Cryptography;
using System.Text;

namespace Relatude.DB.Common;

public static class StringExtentions {
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
}
