using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace KvStore.Internal;

/// <summary>
/// CRC-32C (Castagnoli, reflected polynomial 0x82F63B78) used to checksum WAL transaction
/// records so a torn/partial write is detected on recovery. Castagnoli rather than IEEE because
/// x86 (SSE4.2) and ARM expose it as a single instruction; the table loop is only the fallback
/// for hardware without it. We control both writer and reader, so the polynomial choice is
/// purely internal to the WAL format.
/// </summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        const uint poly = 0x82F63B78u; // CRC-32C, reflected
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;

        if (Sse42.X64.IsSupported)
        {
            ulong crc64 = crc;
            while (data.Length >= 8)
            {
                crc64 = Sse42.X64.Crc32(crc64, Unsafe.ReadUnaligned<ulong>(ref Unsafe.AsRef(in data[0])));
                data = data[8..];
            }
            crc = (uint)crc64;
            foreach (byte b in data)
                crc = Sse42.Crc32(crc, b);
        }
        else if (ArmCrc32.Arm64.IsSupported)
        {
            while (data.Length >= 8)
            {
                crc = ArmCrc32.Arm64.ComputeCrc32C(crc, Unsafe.ReadUnaligned<ulong>(ref Unsafe.AsRef(in data[0])));
                data = data[8..];
            }
            foreach (byte b in data)
                crc = ArmCrc32.ComputeCrc32C(crc, b);
        }
        else
        {
            foreach (byte b in data)
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
