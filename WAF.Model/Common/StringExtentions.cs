using System.Security.Cryptography;
using System.Text;

namespace WAF.Common {
    public static class StringExtentions {
        public static Guid GenerateGuid(this string value) {
            byte[] stringbytes = Encoding.UTF8.GetBytes(value);
            byte[] hashedBytes = SHA1.Create().ComputeHash(stringbytes);
            Array.Resize(ref hashedBytes, 16);
            return new Guid(hashedBytes);            
        }
    }
}
