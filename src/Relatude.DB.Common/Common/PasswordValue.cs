using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relatude.DB.Common;

/// <summary>How a password is kept in the database.</summary>
public enum PasswordStorage {
    /// <summary>As an Argon2id hash: it can be verified, never read back. The default, and the safe choice.</summary>
    Hashed = 0,
    /// <summary>Encrypted with the database's key (AES-256-GCM): it can be verified and read back with that key.</summary>
    Encrypted = 1,
    /// <summary>As written: anyone who can read the database files can read it.</summary>
    PlainText = 2,
}

/// <summary>
/// What a store gives the password values it hands out, so they can be verified and read back with the
/// database's keys, which never leave the store.
/// </summary>
public interface IPasswordProtector {
    /// <summary>Whether the password matches the stored form, a hash or an encrypted password.</summary>
    bool Verify(string stored, string password);
    /// <summary>The password an encrypted stored form holds.</summary>
    string Decrypt(string stored);
    /// <summary>Whether the stored form was made with other settings than the store's now: a hash with weaker
    /// parameters or another pepper, a password encrypted with an older key.</summary>
    bool NeedsRehash(string stored);
}

/// <summary>
/// A password property's value. The store keeps one string: empty for no password, else its first character
/// says how the rest is kept, H for a hash, E for an encrypted password and P for plain text. That string is
/// never sent out of the server: JSON and <see cref="ToString"/> say only whether a password is set and how.
/// <para><see cref="SetPassword"/> and <see cref="Clear"/> only record the change on this value; the store makes
/// the hash or the encryption when the node is written, with the database's keys, and refuses a password the
/// property's rules do not allow. A value written that holds no change leaves the stored password as it is, so
/// an object read before a password changed and saved after it cannot put the old one back.</para>
/// </summary>
[JsonConverter(typeof(PasswordValueJsonConverter))]
public sealed class PasswordValue {
    public const char HashedPrefix = 'H';
    public const char EncryptedPrefix = 'E';
    public const char PlainTextPrefix = 'P';

    enum Change : byte { None, Set, Clear }

    string _stored; // empty when there is no password, else prefixed by how it is kept
    Change _change;
    string? _password; // the password SetPassword gives, until the store has made it into _stored
    PasswordStorage _storage; // the storage SetPassword asks for
    IPasswordProtector? _protector;
    bool _frozen; // a value as the store holds it: shared by every reader, so never changed in place

    PasswordValue(string stored) {
        _stored = stored;
    }

    /// <summary>No password. A new value each time, as <see cref="SetPassword"/> changes the value it is called on.</summary>
    public static PasswordValue Empty => new(string.Empty);
    /// <summary>A value that sets the password when the node it is given to is written.</summary>
    public static PasswordValue Create(string password, PasswordStorage storage = PasswordStorage.Hashed) {
        var value = new PasswordValue(string.Empty);
        value.SetPassword(password, storage);
        return value;
    }

    /// <summary>Whether there is a password, counting a change not yet written.</summary>
    public bool IsSet => _change switch {
        Change.Set => true,
        Change.Clear => false,
        _ => _stored.Length > 0,
    };
    /// <summary>How the password is kept, counting a change not yet written; null when there is none.</summary>
    public PasswordStorage? Storage => _change switch {
        Change.Set => _storage,
        Change.Clear => null,
        _ => StorageOf(_stored),
    };
    /// <summary>Whether <see cref="GetPassword"/> can give the password: one kept encrypted or as plain text.</summary>
    public bool IsPasswordRetrievable => Storage is PasswordStorage.Encrypted or PasswordStorage.PlainText;
    /// <summary>Whether the value holds a change, a password set or cleared, that the store has not written yet.</summary>
    public bool HasChange => _change != Change.None;
    /// <summary>
    /// Whether the password was kept with other settings than the store's now: hashed with weaker parameters or
    /// another pepper, or encrypted with an older key. Setting it again, once it is verified at a sign-in, keeps it
    /// the way the store keeps passwords now.
    /// </summary>
    public bool NeedsRehash => _change == Change.None && _stored.Length > 0 && _stored[0] != PlainTextPrefix && _protector != null && _protector.NeedsRehash(_stored);

    /// <summary>Sets a new password, kept the way <paramref name="storage"/> says, when the node is written.</summary>
    public void SetPassword(string password, PasswordStorage storage = PasswordStorage.Hashed) {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length == 0) throw new ArgumentException("A password cannot be empty. Clear removes the password. ", nameof(password));
        if (!Enum.IsDefined(storage)) throw new ArgumentOutOfRangeException(nameof(storage));
        throwIfFrozen();
        _change = Change.Set;
        _password = password;
        _storage = storage;
    }
    /// <summary>Removes the password when the node is written.</summary>
    public void Clear() {
        throwIfFrozen();
        _change = Change.Clear;
        _password = null;
    }
    /// <summary>
    /// Whether <paramref name="password"/> is the password. False when none is set. A hashed or encrypted
    /// password needs the store's keys, so the value must have been read from a store.
    /// </summary>
    public bool Verify(string password) {
        ArgumentNullException.ThrowIfNull(password);
        switch (_change) {
            case Change.Set: return FixedTimeEquals(_password!, password);
            case Change.Clear: return false;
        }
        if (_stored.Length == 0) return false;
        if (_stored[0] == PlainTextPrefix) return FixedTimeEquals(_stored[1..], password);
        return protector().Verify(_stored, password);
    }
    /// <summary>The password, when it is kept encrypted or as plain text. Throws for a hashed password, which cannot be read back.</summary>
    public string GetPassword() {
        if (!IsSet) throw new InvalidOperationException("No password is set. ");
        if (Storage == PasswordStorage.Hashed) throw new InvalidOperationException("A hashed password cannot be read back, only verified. ");
        if (_change == Change.Set) return _password!;
        if (_stored[0] == PlainTextPrefix) return _stored[1..];
        return protector().Decrypt(_stored);
    }
    public override string ToString() => Storage switch {
        null => "(no password)",
        PasswordStorage.Hashed => "(password set, hashed)",
        PasswordStorage.Encrypted => "(password set, encrypted)",
        _ => "(password set, plain text)",
    };

    IPasswordProtector protector() => _protector ?? throw new InvalidOperationException("The password can only be verified or read on a value read from a store, which has the database's keys. ");
    void throwIfFrozen() {
        if (_frozen) throw new InvalidOperationException("This password value is the one the store holds, shared by every reader. Change a copy, as the objects a store gives out hold. ");
    }

    /// <summary>Compares in a time that says nothing about where the two differ, nor how long they are.</summary>
    public static bool FixedTimeEquals(string a, string b) {
        Span<byte> ha = stackalloc byte[32];
        Span<byte> hb = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(a), ha);
        SHA256.HashData(Encoding.UTF8.GetBytes(b), hb);
        return CryptographicOperations.FixedTimeEquals(ha, hb);
    }
    /// <summary>How a stored form keeps its password; null for the empty one. Throws for a form no store makes.</summary>
    public static PasswordStorage? StorageOf(string stored) {
        if (stored.Length == 0) return null;
        return stored[0] switch {
            HashedPrefix => PasswordStorage.Hashed,
            EncryptedPrefix => PasswordStorage.Encrypted,
            PlainTextPrefix => PasswordStorage.PlainText,
            _ => throw new FormatException("Not a stored password. "),
        };
    }

    // For the store. Application code has no use for these: a password is set with SetPassword and checked with
    // Verify, and the stored form goes nowhere else.

    /// <summary>The value as a store holds it. Frozen: it is shared by every reader of the node.</summary>
    public static PasswordValue FromStored(string stored) {
        ArgumentNullException.ThrowIfNull(stored);
        StorageOf(stored); // throws for anything but a stored form
        return new PasswordValue(stored) { _frozen = true };
    }
    /// <summary>The stored form of a value that holds no change.</summary>
    public static string GetStored(PasswordValue value) {
        if (value._change != Change.None) throw new InvalidOperationException("The password value holds a change the store has not written. ");
        return value._stored;
    }
    /// <summary>The password and storage a value sets, when it holds that change.</summary>
    public static bool TryGetNewPassword(PasswordValue value, out string password, out PasswordStorage storage) {
        password = value._password ?? string.Empty;
        storage = value._storage;
        return value._change == Change.Set;
    }
    /// <summary>Whether the value removes the password.</summary>
    public static bool IsCleared(PasswordValue value) => value._change == Change.Clear;
    /// <summary>
    /// A copy for an object a store gives out, which can verify and read the password with the store's keys and be
    /// changed without changing what the store holds.
    /// </summary>
    public static PasswordValue CopyForReader(PasswordValue value, IPasswordProtector? protector) => new(value._stored) {
        _change = value._change,
        _password = value._password,
        _storage = value._storage,
        _protector = protector ?? value._protector,
    };
    /// <summary>Whether two values hold the same stored form and the same change.</summary>
    public static bool AreValuesEqual(PasswordValue a, PasswordValue b) =>
        a._change == b._change && a._storage == b._storage && string.Equals(a._stored, b._stored, StringComparison.Ordinal) && string.Equals(a._password, b._password, StringComparison.Ordinal);
}

/// <summary>
/// JSON shape: {"isSet": true, "storage": "Hashed"}, never the password or its stored form. Read back, that shape
/// is a value with no change, which leaves a stored password as it is; {"set": "...", "storage": "Hashed"} sets
/// a password (storage optional, Hashed when left out) and {"clear": true} removes it.
/// </summary>
public sealed class PasswordValueJsonConverter : JsonConverter<PasswordValue> {
    public override bool HandleNull => true;
    public override PasswordValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.Null) return PasswordValue.Empty;
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected an object or null for a password. ");
        string? password = null;
        var storage = PasswordStorage.Hashed;
        var clear = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject) {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "set", StringComparison.OrdinalIgnoreCase)) {
                password = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            } else if (string.Equals(name, "storage", StringComparison.OrdinalIgnoreCase)) {
                if (reader.TokenType == JsonTokenType.String) {
                    if (!Enum.TryParse(reader.GetString(), true, out storage) || !Enum.IsDefined(storage)) throw new JsonException("Unknown password storage. ");
                } else if (reader.TokenType != JsonTokenType.Null) {
                    throw new JsonException("Password storage is a name: Hashed, Encrypted or PlainText. ");
                }
            } else if (string.Equals(name, "clear", StringComparison.OrdinalIgnoreCase)) {
                clear = reader.TokenType == JsonTokenType.True;
            } else {
                reader.Skip();
            }
        }
        if (password != null && clear) throw new JsonException("A password cannot be both set and cleared. ");
        if (password != null) return PasswordValue.Create(password, storage);
        var value = PasswordValue.Empty;
        if (clear) value.Clear();
        return value;
    }
    public override void Write(Utf8JsonWriter writer, PasswordValue? value, JsonSerializerOptions options) {
        value ??= PasswordValue.Empty;
        writer.WriteStartObject();
        writer.WriteBoolean("isSet", value.IsSet);
        if (value.Storage is { } storage) writer.WriteString("storage", storage.ToString());
        else writer.WriteNull("storage");
        writer.WriteEndObject();
    }
}
