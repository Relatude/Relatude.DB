using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Relatude.DB.Common;

namespace Relatude.DB.DataStores;

/// <summary>
/// Makes and reads the stored forms of passwords with a database's keys (<see cref="PasswordSettings"/>):
/// <list type="bullet">
/// <item>H$argon2id$v=19$m=19456,t=2,p=1[,keyid=K]$salt$hash: an Argon2id hash in the PHC string format, with
/// its parameters, so they can be raised later, and the fingerprint of the pepper it was made with, if any;</item>
/// <item>E$K$data: AES-256-GCM, K the fingerprint of the key and data the nonce, tag and ciphertext;</item>
/// <item>P followed by the password.</item>
/// </list>
/// A fingerprint is the first four bytes of the key's SHA-256, enough to find the key among a few, too few to
/// say anything of it. Base64 throughout is without padding, as PHC strings are.
/// </summary>
internal sealed class PasswordProtector : IPasswordProtector {
    const int saltSize = 16;
    const int hashSize = 32;
    const int nonceSize = 12;
    const int tagSize = 16;
    const string algorithm = "argon2id";
    const int argon2Version = 19; // 0x13, the version Konscious implements
    readonly int _memory;
    readonly int _iterations;
    readonly int _parallelism;
    readonly byte[]? _pepper;
    readonly string? _pepperId;
    readonly byte[]? _key;
    readonly string? _keyId;
    readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal); // every key a password may be encrypted with, by fingerprint

    public PasswordProtector(PasswordSettings? settings) {
        settings ??= new();
        _parallelism = Math.Clamp(settings.HashParallelism, 1, 64);
        _iterations = Math.Max(1, settings.HashIterations);
        _memory = Math.Max(8 * _parallelism, settings.HashMemoryKiB); // Argon2's least
        if (!string.IsNullOrWhiteSpace(settings.Pepper)) {
            _pepper = KeyBytes(settings.Pepper);
            _pepperId = Fingerprint(_pepper);
        }
        if (settings.OldEncryptionKeys != null) {
            foreach (var old in settings.OldEncryptionKeys) {
                if (string.IsNullOrWhiteSpace(old)) continue;
                var bytes = KeyBytes(old);
                _keys[Fingerprint(bytes)] = bytes;
            }
        }
        if (!string.IsNullOrWhiteSpace(settings.EncryptionKey)) {
            _key = KeyBytes(settings.EncryptionKey);
            _keyId = Fingerprint(_key);
            _keys[_keyId] = _key;
        }
    }

    public bool CanEncrypt => _key != null;

    /// <summary>The stored form of a password, kept as <paramref name="storage"/> says.</summary>
    public string Protect(string password, PasswordStorage storage) => storage switch {
        PasswordStorage.Hashed => Hash(password),
        PasswordStorage.Encrypted => Encrypt(password),
        PasswordStorage.PlainText => PasswordValue.PlainTextPrefix + password,
        _ => throw new ArgumentOutOfRangeException(nameof(storage)),
    };
    public string Hash(string password) {
        var salt = RandomNumberGenerator.GetBytes(saltSize);
        var hash = argon2(password, salt, _memory, _iterations, _parallelism, _pepper);
        var parameters = "m=" + _memory.ToString(CultureInfo.InvariantCulture) + ",t=" + _iterations.ToString(CultureInfo.InvariantCulture) + ",p=" + _parallelism.ToString(CultureInfo.InvariantCulture);
        if (_pepperId != null) parameters += ",keyid=" + _pepperId;
        return PasswordValue.HashedPrefix + "$" + algorithm + "$v=" + argon2Version.ToString(CultureInfo.InvariantCulture) + "$" + parameters + "$" + B64(salt) + "$" + B64(hash);
    }
    public string Encrypt(string password) {
        if (_key == null) throw new InvalidOperationException("Passwords cannot be kept encrypted: the database has no encryption key (Passwords.EncryptionKey in its settings). ");
        var plain = Encoding.UTF8.GetBytes(password);
        var data = new byte[nonceSize + tagSize + plain.Length];
        var nonce = data.AsSpan(0, nonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(_key, tagSize)) {
            aes.Encrypt(nonce, plain, data.AsSpan(nonceSize + tagSize), data.AsSpan(nonceSize, tagSize));
        }
        CryptographicOperations.ZeroMemory(plain);
        return PasswordValue.EncryptedPrefix + "$" + _keyId + "$" + B64(data);
    }

    public bool Verify(string stored, string password) {
        if (stored.Length == 0) return false;
        switch (stored[0]) {
            case PasswordValue.PlainTextPrefix:
                return PasswordValue.FixedTimeEquals(stored[1..], password);
            case PasswordValue.EncryptedPrefix:
                return PasswordValue.FixedTimeEquals(Decrypt(stored), password);
            case PasswordValue.HashedPrefix: {
                    var h = parseHash(stored);
                    byte[]? pepper = null;
                    if (h.PepperId != null) {
                        if (h.PepperId != _pepperId) throw new InvalidOperationException("The password was hashed with a pepper the database's settings no longer have (Passwords.Pepper). ");
                        pepper = _pepper;
                    }
                    var hash = argon2(password, h.Salt, h.Memory, h.Iterations, h.Parallelism, pepper, h.Hash.Length);
                    return CryptographicOperations.FixedTimeEquals(hash, h.Hash);
                }
            default:
                throw new FormatException("Not a stored password. ");
        }
    }
    public string Decrypt(string stored) {
        var parts = stored.Split('$');
        if (parts.Length != 3 || parts[0] != PasswordValue.EncryptedPrefix.ToString()) throw new FormatException("Not an encrypted password. ");
        if (!_keys.TryGetValue(parts[1], out var key)) throw new InvalidOperationException("The password was encrypted with a key the database's settings do not have (Passwords.EncryptionKey or OldEncryptionKeys). ");
        var data = FromB64(parts[2]);
        if (data.Length < nonceSize + tagSize) throw new FormatException("Not an encrypted password. ");
        var plain = new byte[data.Length - nonceSize - tagSize];
        try {
            using var aes = new AesGcm(key, tagSize);
            aes.Decrypt(data.AsSpan(0, nonceSize), data.AsSpan(nonceSize + tagSize), data.AsSpan(nonceSize, tagSize), plain);
        } catch (CryptographicException ex) {
            throw new InvalidOperationException("The encrypted password could not be decrypted: it has been changed, or the key with its fingerprint is not the one it was encrypted with. ", ex);
        }
        var password = Encoding.UTF8.GetString(plain);
        CryptographicOperations.ZeroMemory(plain);
        return password;
    }
    public bool NeedsRehash(string stored) {
        if (stored.Length == 0) return false;
        switch (stored[0]) {
            case PasswordValue.HashedPrefix:
                var h = parseHash(stored);
                return h.Memory != _memory || h.Iterations != _iterations || h.Parallelism != _parallelism || h.PepperId != _pepperId;
            case PasswordValue.EncryptedPrefix:
                var parts = stored.Split('$');
                return parts.Length != 3 || parts[1] != _keyId;
            default:
                return false;
        }
    }

    record struct ParsedHash(int Memory, int Iterations, int Parallelism, string? PepperId, byte[] Salt, byte[] Hash);
    static ParsedHash parseHash(string stored) {
        // H $ argon2id $ v=19 $ m=..,t=..,p=..[,keyid=..] $ salt $ hash
        var parts = stored.Split('$');
        if (parts.Length != 6 || parts[0] != PasswordValue.HashedPrefix.ToString() || parts[1] != algorithm) throw new FormatException("Not an Argon2id password hash. ");
        if (parts[2] != "v=" + argon2Version.ToString(CultureInfo.InvariantCulture)) throw new FormatException("Unsupported Argon2 version: " + parts[2] + ". ");
        int memory = 0, iterations = 0, parallelism = 0;
        string? pepperId = null;
        foreach (var pair in parts[3].Split(',')) {
            var eq = pair.IndexOf('=');
            if (eq < 1) throw new FormatException("Bad Argon2 parameters. ");
            var name = pair[..eq];
            var value = pair[(eq + 1)..];
            switch (name) {
                case "m": memory = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "t": iterations = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "p": parallelism = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "keyid": pepperId = value; break;
                default: throw new FormatException("Unknown Argon2 parameter: " + name + ". ");
            }
        }
        if (memory < 8 || iterations < 1 || parallelism < 1) throw new FormatException("Bad Argon2 parameters. ");
        return new(memory, iterations, parallelism, pepperId, FromB64(parts[4]), FromB64(parts[5]));
    }
    static byte[] argon2(string password, byte[] salt, int memory, int iterations, int parallelism, byte[]? pepper, int length = hashSize) {
        var bytes = Encoding.UTF8.GetBytes(password);
        try {
            using var a = new Argon2id(bytes) {
                Salt = salt,
                MemorySize = memory,
                Iterations = iterations,
                DegreeOfParallelism = parallelism,
            };
            if (pepper != null) a.KnownSecret = pepper;
            return a.GetBytes(length);
        } finally {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>A key: 32 bytes given in base64, else the SHA-256 of the text.</summary>
    public static byte[] KeyBytes(string key) {
        key = key.Trim();
        var buffer = new byte[key.Length];
        if (Convert.TryFromBase64String(key, buffer, out var written) && written == 32) return buffer[..32];
        return SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }
    static string Fingerprint(byte[] key) => B64(SHA256.HashData(key).AsSpan(0, 4));
    static string B64(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=');
    static byte[] FromB64(string s) {
        var padded = s.Length % 4 == 0 ? s : s + new string('=', 4 - s.Length % 4);
        return Convert.FromBase64String(padded);
    }
}
