using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
namespace Relatude.DB.NodeServer;
internal static class StringEncryption {
    public static string Encrypt(string clearText, string secret, string salt) {
        return encryptString(clearText, secret, salt);
    }
    public static bool TryDecrypt(string cipherText, string secret, string salt, [MaybeNullWhen(false)] out string result) {
        try {
            result = decryptString(cipherText, secret, salt);
            return true;
        } catch {
            result = null;
            return false;
        }
    }
    static string encryptString(string clearText, string password, string salt) {
        var clearBytes = WAFGlobals.Encoding.GetBytes(clearText);
        var saltBytes = WAFGlobals.Encoding.GetBytes(salt);
        var pdb = new PasswordDeriveBytes(password, saltBytes);
        var Key = pdb.GetBytes(32);
        var IV = pdb.GetBytes(16);
        var encryptedData = encryptBytes(clearBytes, Key, IV);
        return Convert.ToBase64String(encryptedData);
    }
    static string decryptString(string cipherText, string password, string salt) {
        var cipherBytes = Convert.FromBase64String(cipherText);
        var saltBytes = WAFGlobals.Encoding.GetBytes(salt);
        var pdb = new PasswordDeriveBytes(password, saltBytes);
        byte[] decryptedData = decryptBytes(cipherBytes, pdb.GetBytes(32), pdb.GetBytes(16));
        return WAFGlobals.Encoding.GetString(decryptedData);
    }
    static byte[] encryptBytes(byte[] clearData, byte[] Key, byte[] IV) {
        var ms = new MemoryStream();
#pragma warning disable SYSLIB0022 // Type or member is obsolete
        var alg = Rijndael.Create();
#pragma warning restore SYSLIB0022 // Type or member is obsolete
        alg.Key = Key;
        alg.IV = IV;
        CryptoStream cs = new CryptoStream(ms, alg.CreateEncryptor(), CryptoStreamMode.Write);
        cs.Write(clearData, 0, clearData.Length);
        cs.Close();
        return ms.ToArray();
    }
    static byte[] decryptBytes(byte[] cipherData, byte[] Key, byte[] IV) {
        try {
            var ms = new MemoryStream();
#pragma warning disable SYSLIB0022 // Type or member is obsolete
            var alg = Rijndael.Create();
#pragma warning restore SYSLIB0022 // Type or member is obsolete
            alg.Key = Key;
            alg.IV = IV;
            var cs = new CryptoStream(ms, alg.CreateDecryptor(), CryptoStreamMode.Write);
            cs.Write(cipherData, 0, cipherData.Length);
            cs.Close();
            byte[] decryptedData = ms.ToArray();
            return decryptedData;
        } catch (Exception e) {
            throw new DecryptionException("Decryption failed. ", e);
        }
    }

    internal static bool TryDecrypt(string token, object tokenEncryptionSecret, object tokenEncryptionSalt, out string json) {
        throw new NotImplementedException();
    }
}
public class DecryptionException : Exception {
    public DecryptionException(string message, Exception innerException) : base(message, innerException) { }
}