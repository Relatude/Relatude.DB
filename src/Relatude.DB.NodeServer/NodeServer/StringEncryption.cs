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
