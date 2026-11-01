using System.Globalization;
using System.Text.RegularExpressions;
using Relatude.DB.Common;
namespace Relatude.DB.Datamodels.Properties;
/// <summary>
/// A password (<see cref="PasswordValue"/>). Never indexed, unique, a facet, in the text or semantic index, part
/// of the display name or per culture: the only question a query may ask of it is whether it is set. Which ways
/// of keeping it are allowed, and what a new password must be like, are checked when one is set.
/// </summary>
public class PasswordPropertyModel : PropertyModel {
    public override bool ExcludeFromTextIndex { get => true; set { } }
    public override PropertyType PropertyType { get => PropertyType.Password; }
    public override object GetDefaultValue() => PasswordValue.Empty;
    public override string? GetDefaultDeclaration() => typeof(PasswordValue).FullName + "." + nameof(PasswordValue.Empty);
    public override string GetDefaultValueAsCode() => typeof(PasswordValue).FullName + "." + nameof(PasswordValue.Empty);

    public bool AllowHashed { get; set; } = true;
    public bool AllowEncrypted { get; set; }
    public bool AllowPlainText { get; set; }
    public int MinLength { get; set; } = DefaultMinLength;
    public int MaxLength { get; set; } = DefaultMaxLength;
    public bool RequireUppercase { get; set; }
    public bool RequireLowercase { get; set; }
    public bool RequireDigit { get; set; }
    public bool RequireSymbol { get; set; }
    public string? RegularExpression { get; set; }
    public string? RegularExpressionMessage { get; set; }
    public const int DefaultMinLength = 8;
    public const int DefaultMaxLength = 128;

    public bool Allows(PasswordStorage storage) => storage switch {
        PasswordStorage.Hashed => AllowHashed,
        PasswordStorage.Encrypted => AllowEncrypted,
        PasswordStorage.PlainText => AllowPlainText,
        _ => false,
    };
    /// <summary>Why the password cannot be set, or null when it can. The reason never quotes the password.</summary>
    public string? CheckPassword(string password, PasswordStorage storage) {
        if (!Allows(storage)) return CodeName + " cannot be kept " + describe(storage) + ". " + allowedText();
        if (password.Length < MinLength) return CodeName + " must have at least " + count(MinLength) + ". ";
        if (password.Length > MaxLength) return CodeName + " can have at most " + count(MaxLength) + ". ";
        if (RequireUppercase && !password.Any(char.IsUpper)) return CodeName + " must have an upper case letter. ";
        if (RequireLowercase && !password.Any(char.IsLower)) return CodeName + " must have a lower case letter. ";
        if (RequireDigit && !password.Any(char.IsDigit)) return CodeName + " must have a digit. ";
        if (RequireSymbol && password.All(char.IsLetterOrDigit)) return CodeName + " must have a character that is neither a letter nor a digit. ";
        if (!string.IsNullOrEmpty(RegularExpression)) {
            // a new password is checked before the store takes its lock, but a pattern that backtracks without end
            // must still fail rather than hold the writer
            var matches = Regex.IsMatch(password, RegularExpression, RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!matches) {
                var message = RegularExpressionMessage?.Trim();
                return string.IsNullOrEmpty(message) ? CodeName + " is not in the expected format. " : CodeName + ": " + (message.EndsWith('.') ? message : message + ".") + " ";
            }
        }
        return null;
    }
    string allowedText() {
        var allowed = new[] { PasswordStorage.Hashed, PasswordStorage.Encrypted, PasswordStorage.PlainText }.Where(Allows).Select(describe).ToArray();
        return allowed.Length == 0 ? "It allows no way of keeping a password. " : "It can be kept " + string.Join(" or ", allowed) + ". ";
    }
    static string describe(PasswordStorage storage) => storage switch {
        PasswordStorage.Hashed => "hashed",
        PasswordStorage.Encrypted => "encrypted",
        _ => "as plain text",
    };
    static string count(int n) => n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " character" : " characters");

    /// <summary>A password value, or <see cref="PasswordValue.Empty"/> for null. Anything else is refused: a string must
    /// never become a password, nor take one away, without anyone saying so.</summary>
    public static PasswordValue ForceValueType(object? value, out bool changed) {
        if (value is PasswordValue password) {
            changed = false;
            return password;
        }
        if (value == null) {
            changed = true;
            return PasswordValue.Empty;
        }
        throw new ArgumentException("A password property takes a PasswordValue, not a " + value.GetType().Name + ". Use PasswordValue.Create or SetPassword. ");
    }
    // node data codec: a version byte, then the stored form as UTF-8. A value holding a change, a password not yet
    // hashed or encrypted, is never written: the codec writes the log too, and a password must not reach it. So a
    // password cannot be set through a remote store yet, which sends nodes with this codec.
    const byte codecVersion = 1;
    public static byte[] GetBytes(PasswordValue value) {
        if (value.HasChange) throw new InvalidOperationException("A password change cannot be written as bytes: only the store that keeps the node, with the database's keys, can set a password. ");
        var stored = PasswordValue.GetStored(value);
        var bytes = new byte[1 + System.Text.Encoding.UTF8.GetByteCount(stored)];
        bytes[0] = codecVersion;
        System.Text.Encoding.UTF8.GetBytes(stored, 0, stored.Length, bytes, 1);
        return bytes;
    }
    public static PasswordValue GetValue(byte[] bytes) {
        if (bytes.Length == 0) return PasswordValue.FromStored(string.Empty);
        if (bytes[0] != codecVersion) throw new NotSupportedException("Version " + bytes[0] + " of the password value is not supported. ");
        return PasswordValue.FromStored(System.Text.Encoding.UTF8.GetString(bytes, 1, bytes.Length - 1));
    }
}
