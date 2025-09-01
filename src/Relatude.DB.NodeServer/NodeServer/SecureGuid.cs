using System.Security.Cryptography;
namespace Relatude.DB.NodeServer;
public class SecureGuid {
    /// <summary>
    /// Create a new `Guid` with `Version 4` and `RFC 4122` compliant.
    /// </summary>
    ///  <returns>A new `Guid` with `Version 4` and `RFC 4122` compliant.</returns>
    public static Guid New() {
        // Byte indices
        int versionByteIndex = BitConverter.IsLittleEndian ? 7 : 6;
        const int variantByteIndex = 8;

        // Version mask & shift for `Version 4`
        const int versionMask = 0x0F;
        const int versionShift = 0x40;

        // Variant mask & shift for `RFC 4122`
        const int variantMask = 0x3F;
        const int variantShift = 0x80;

        // Get bytes of cryptographically-strong random values
        var bytes = new byte[16];

        RandomNumberGenerator.Fill(bytes);

        // Set version bits -- 6th or 7th byte according to Endianness, big or little Endian respectively
        bytes[versionByteIndex] = (byte)(bytes[versionByteIndex] & versionMask | versionShift);

        // Set variant bits -- 8th byte
        bytes[variantByteIndex] = (byte)(bytes[variantByteIndex] & variantMask | variantShift);

        // Initialize Guid from the modified random bytes
        return new Guid(bytes);
    }

}
