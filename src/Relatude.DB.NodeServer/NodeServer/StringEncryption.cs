using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
namespace Relatude.DB.NodeServer;

internal static class StringEncryption {
    private const int KeySize = 32;      // 256 bits
    private const int NonceSize = 12;    // AES-GCM standard
    private const int TagSize = 16;      // AES-GCM standard
    private const int SaltSize = 16;     // Standard salt length
    private const int Iterations = 600_000; // Updated for 2026 standards

    public static string Encrypt(string clearText, string secret) {
        // Use UTF8 for consistent byte representation
        byte[] plaintext = Encoding.UTF8.GetBytes(clearText);

        // Generate a cryptographically strong random salt
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = DeriveKey(secret, salt);

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize)) {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        // Combine all parts into one array: [Salt][Nonce][Tag][Ciphertext]
        byte[] result = new byte[SaltSize + NonceSize + TagSize + ciphertext.Length];

        var span = result.AsSpan();
        salt.CopyTo(span.Slice(0, SaltSize));
        nonce.CopyTo(span.Slice(SaltSize, NonceSize));
        tag.CopyTo(span.Slice(SaltSize + NonceSize, TagSize));
        ciphertext.CopyTo(span.Slice(SaltSize + NonceSize + TagSize));

        return Convert.ToBase64String(result);
    }
    public static bool TryDecrypt(string cipherText, string secret, [MaybeNullWhen(false)] out string result) {
        try {
            byte[] data = Convert.FromBase64String(cipherText);

            // Basic length check to avoid Span exceptions
            if (data.Length < SaltSize + NonceSize + TagSize) {
                result = null;
                return false;
            }

            var span = data.AsSpan();

            // Extract the components using Spans (efficient, no allocations)
            var salt = span.Slice(0, SaltSize).ToArray();
            var nonce = span.Slice(SaltSize, NonceSize);
            var tag = span.Slice(SaltSize + NonceSize, TagSize);
            var ciphertext = span.Slice(SaltSize + NonceSize + TagSize);

            byte[] key = DeriveKey(secret, salt);
            byte[] plaintext = new byte[ciphertext.Length];

            using (var aes = new AesGcm(key, TagSize)) {
                // Decrypt will throw if the 'Tag' doesn't match (tampering detection)
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }
            result = Encoding.UTF8.GetString(plaintext);
            return true;
        } catch (Exception ex) when (ex is CryptographicException or FormatException) {
            // Decryption failed: either bad password, tampered data, or bad Base64
            result = null;
            return false;
        }
    }

    private static byte[] DeriveKey(string password, byte[] salt) {
        // Static helper method for PBKDF2 is cleaner and safer
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            KeySize);
    }
}


//internal static class StringEncryption {
//    // Sizes recommended for GCM
//    private const int NonceSize = 12;   // 96 bit
//    private const int TagSize = 16;     // 128 bit
//    private const int KeySize = 32;     // 256 bit
//    private const int Pbkdf2Iterations = 100_000;

//    public static string Encrypt(string clearText, string secret, string salt) {
//        var clearBytes = RelatudeDBGlobals.Encoding.GetBytes(clearText);
//        var saltBytes = RelatudeDBGlobals.Encoding.GetBytes(salt);

//        // Derive key
//        using var kdf = new Rfc2898DeriveBytes(secret, saltBytes, Pbkdf2Iterations, HashAlgorithmName.SHA256);
//        var key = kdf.GetBytes(KeySize);

//        // Random nonce PER encryption
//        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);

//        byte[] cipherBytes = new byte[clearBytes.Length];
//        byte[] tag = new byte[TagSize];

//        using (var aes = new AesGcm(key, TagSize)) {
//            aes.Encrypt(nonce, clearBytes, cipherBytes, tag);
//        }

//        // Layout: [nonce | tag | ciphertext]
//        var result = new byte[NonceSize + TagSize + cipherBytes.Length];
//        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
//        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
//        Buffer.BlockCopy(cipherBytes, 0, result, NonceSize + TagSize, cipherBytes.Length);

//        return Convert.ToBase64String(result);
//    }

//    public static bool TryDecrypt(string cipherText, string secret, string salt, [MaybeNullWhen(false)] out string result) {
//        try {
//            var allBytes = Convert.FromBase64String(cipherText);
//            if (allBytes.Length < NonceSize + TagSize)
//                throw new DecryptionException("Ciphertext too short.", null);

//            var saltBytes = RelatudeDBGlobals.Encoding.GetBytes(salt);

//            using var kdf = new Rfc2898DeriveBytes(secret, saltBytes, Pbkdf2Iterations, HashAlgorithmName.SHA256);
//            var key = kdf.GetBytes(KeySize);

//            byte[] nonce = new byte[NonceSize];
//            byte[] tag = new byte[TagSize];
//            byte[] cipherBytes = new byte[allBytes.Length - NonceSize - TagSize];

//            Buffer.BlockCopy(allBytes, 0, nonce, 0, NonceSize);
//            Buffer.BlockCopy(allBytes, NonceSize, tag, 0, TagSize);
//            Buffer.BlockCopy(allBytes, NonceSize + TagSize, cipherBytes, 0, cipherBytes.Length);

//            byte[] clearBytes = new byte[cipherBytes.Length];

//            using (var aes = new AesGcm(key, TagSize)) {
//                aes.Decrypt(nonce, cipherBytes, tag, clearBytes);
//            }

//            result = RelatudeDBGlobals.Encoding.GetString(clearBytes);
//            return true;
//        } catch {
//            result = null;
//            return false;
//        }
//    }
//}

//public class DecryptionException : Exception {
//    public DecryptionException(string message, Exception? innerException) : base(message, innerException) { }
//}

//internal static class StringEncryption {
//    public static string Encrypt(string clearText, string secret, string salt) {
//        return encryptString(clearText, secret, salt);
//    }
//    public static bool TryDecrypt(string cipherText, string secret, string salt, [MaybeNullWhen(false)] out string result) {
//        try {
//            result = decryptString(cipherText, secret, salt);
//            return true;
//        } catch {
//            result = null;
//            return false;
//        }
//    }
//    static string encryptString(string clearText, string password, string salt) {
//        var clearBytes = RelatudeDBGlobals.Encoding.GetBytes(clearText);
//        var saltBytes = RelatudeDBGlobals.Encoding.GetBytes(salt);
//        var pdb = new PasswordDeriveBytes(password, saltBytes);
//        var Key = pdb.GetBytes(32);
//        var IV = pdb.GetBytes(16);
//        var encryptedData = encryptBytes(clearBytes, Key, IV);
//        return Convert.ToBase64String(encryptedData);
//    }
//    static string decryptString(string cipherText, string password, string salt) {
//        var cipherBytes = Convert.FromBase64String(cipherText);
//        var saltBytes = RelatudeDBGlobals.Encoding.GetBytes(salt);
//        var pdb = new PasswordDeriveBytes(password, saltBytes);
//        byte[] decryptedData = decryptBytes(cipherBytes, pdb.GetBytes(32), pdb.GetBytes(16));
//        return RelatudeDBGlobals.Encoding.GetString(decryptedData);
//    }
//    static byte[] encryptBytes(byte[] clearData, byte[] Key, byte[] IV) {
//        var ms = new MemoryStream();
//#pragma warning disable SYSLIB0022 // Type or member is obsolete
//        var alg = Rijndael.Create();
//#pragma warning restore SYSLIB0022 // Type or member is obsolete
//        alg.Key = Key;
//        alg.IV = IV;
//        CryptoStream cs = new CryptoStream(ms, alg.CreateEncryptor(), CryptoStreamMode.Write);
//        cs.Write(clearData, 0, clearData.Length);
//        cs.Close();
//        return ms.ToArray();
//    }
//    static byte[] decryptBytes(byte[] cipherData, byte[] Key, byte[] IV) {
//        try {
//            var ms = new MemoryStream();
//#pragma warning disable SYSLIB0022 // Type or member is obsolete
//            var alg = Rijndael.Create();
//#pragma warning restore SYSLIB0022 // Type or member is obsolete
//            alg.Key = Key;
//            alg.IV = IV;
//            var cs = new CryptoStream(ms, alg.CreateDecryptor(), CryptoStreamMode.Write);
//            cs.Write(cipherData, 0, cipherData.Length);
//            cs.Close();
//            byte[] decryptedData = ms.ToArray();
//            return decryptedData;
//        } catch (Exception e) {
//            throw new DecryptionException("Decryption failed. ", e);
//        }
//    }

//    internal static bool TryDecrypt(string token, object tokenEncryptionSecret, object tokenEncryptionSalt, out string json) {
//        throw new NotImplementedException();
//    }
//}
//public class DecryptionException : Exception {
//    public DecryptionException(string message, Exception innerException) : base(message, innerException) { }
//}