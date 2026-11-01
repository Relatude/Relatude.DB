namespace Relatude.DB.DataStores;

/// <summary>
/// How a database keeps the passwords of its password properties (<see cref="Relatude.DB.Common.PasswordValue"/>).
/// The keys are secrets: keep them in configuration or user secrets, as the server never writes a value from
/// there to its settings files. A key is 32 random bytes in base64; any other text is accepted and made into a
/// key by SHA-256, which is only as strong as the text.
/// </summary>
public class PasswordSettings {
    /// <summary>
    /// The key passwords kept encrypted are encrypted with (AES-256-GCM). Without one no password can be kept
    /// encrypted. Losing it loses every encrypted password; changing it, keep the old one in
    /// <see cref="OldEncryptionKeys"/> so the passwords encrypted with it can still be read.
    /// </summary>
    public string? EncryptionKey { get; set; }
    /// <summary>Keys passwords were encrypted with before <see cref="EncryptionKey"/>, read with but never encrypted with.</summary>
    public string[]? OldEncryptionKeys { get; set; }
    /// <summary>
    /// A secret every hash is made with besides its salt (Argon2's secret), so the hashes are of no use to whoever
    /// has the database without it. Optional. Losing or changing it makes every password hashed with it fail to
    /// verify, so it is set once, before the first password.
    /// </summary>
    public string? Pepper { get; set; }
    /// <summary>The memory one hash takes, in KiB (Argon2's m). The default, 19 MiB, is OWASP's least for Argon2id.</summary>
    public int HashMemoryKiB { get; set; } = DefaultHashMemoryKiB;
    /// <summary>The passes over that memory (Argon2's t).</summary>
    public int HashIterations { get; set; } = DefaultHashIterations;
    /// <summary>The lanes, and so the threads, a hash uses (Argon2's p).</summary>
    public int HashParallelism { get; set; } = DefaultHashParallelism;

    public const int DefaultHashMemoryKiB = 19456;
    public const int DefaultHashIterations = 2;
    public const int DefaultHashParallelism = 1;
}
