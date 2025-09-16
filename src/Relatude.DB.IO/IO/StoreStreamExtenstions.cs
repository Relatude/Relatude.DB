using System.Buffers.Binary;
using System.Text;
namespace Relatude.DB.IO;
public static class StoreStreamExtenstions {

    public static void WriteVerifiedInt(this IAppendStream s, int v) {
        s.Append(BitConverter.GetBytes(v));
        s.Append(BitConverter.GetBytes(-v));
    }
    public static int ReadVerifiedInt(this IReadStream s) {
        var v1 = BitConverter.ToInt32(s.Read(4), 0);
        var v2 = BitConverter.ToInt32(s.Read(4), 0);
        if (v1 != -v2) throw new Exception("Verification failed. Invalid binary data. ");
        return v1;
    }
    public static void WriteVerifiedUInt(this IAppendStream s, uint v) {
        s.Append(BitConverter.GetBytes(v));
        s.Append(BitConverter.GetBytes(-v));
    }
    public static uint ReadVerifiedUInt(this IReadStream s) {
        var v1 = BitConverter.ToUInt32(s.Read(4), 0);
        var v2 = BitConverter.ToUInt32(s.Read(4), 0);
        if (v1 != -v2) throw new Exception("Verification failed. Invalid binary data. ");
        return v1;
    }

    public static void WriteVerifiedLong(this IAppendStream s, long v) {
        s.Append(BitConverter.GetBytes(v));
        s.Append(BitConverter.GetBytes(-v));
    }
    public static long ReadVerifiedLong(this IReadStream s) {
        var v1 = BitConverter.ToInt64(s.Read(8), 0);
        var v2 = BitConverter.ToInt64(s.Read(8), 0);
        if (v1 != -v2) throw new Exception("Verification failed. Invalid binary data. ");
        return v1;
    }

    public static void WriteBool(this IAppendStream s, bool v) {
        // I know, wasting space....
        s.Append(new byte[1] { (byte)(v ? 1 : 0) });
    }
    public static bool ReadBool(this IReadStream s) {
        return s.Read(1)[0] == 1;
    }

    public static void WriteOneByte(this IAppendStream s, byte v) {
        s.Append(new byte[1] { v });
    }
    public static byte ReadOneByte(this IReadStream s) {
        return s.Read(1)[0];
    }

    public static void WriteUInt(this IAppendStream s, uint v) {
        s.Append(BitConverter.GetBytes(v));
    }
    public static uint ReadUInt(this IReadStream s) {
        return BitConverter.ToUInt32(s.Read(4), 0);
    }

    public static void WriteInt(this IAppendStream s, int v) {
        s.Append(BitConverter.GetBytes(v));
    }
    public static int ReadInt(this IReadStream s) {
        return BitConverter.ToInt32(s.Read(4), 0);
    }

    public static void WriteLong(this IAppendStream s, long v) {
        s.Append(BitConverter.GetBytes(v));
    }
    public static long ReadLong(this IReadStream s) {
        return BitConverter.ToInt64(s.Read(8), 0);
    }

    public static byte[] GetByteArray(this IAppendStream s, ref long position) {
        var length = GetVerifiedInt(s, ref position);
        var buffer = new byte[length];
        s.Get(position, length, buffer);
        position += length;
        return buffer;
    }
    public static string GetString(this IAppendStream s, ref long position) {
        var length = GetVerifiedInt(s, ref position);
        var buffer = new byte[length];
        s.Get(position, length, buffer);
        position += length;
        return RelatudeDBGlobals.Encoding.GetString(buffer);
    }
    public static int GetInt(this IAppendStream s, long position) {
        var bytes = new byte[4];
        s.Get(position, 4, bytes);
        return BitConverter.ToInt32(bytes, 0);
    }
    public static long GetLong(this IAppendStream s, long position) {
        var bytes = new byte[8];
        s.Get(position, 8, bytes);
        return BitConverter.ToInt64(bytes, 0);
    }
    public static int GetVerifiedInt(this IAppendStream s, ref long position) {
        var int1 = GetInt(s, position);
        var int2 = GetInt(s, position + 4);
        if (int1 != -int2) throw new Exception("Verification failed. Invalid binary data. ");
        position += 8;
        return int1;
    }
    public static long GetVerifiedLong(this IAppendStream s, ref long position) {
        var long1 = GetLong(s, position);
        var long2 = GetLong(s, position + 8);
        if (long1 != -long2) throw new Exception("Verification failed. Invalid binary data. ");
        position += 16;
        return long1;
    }
    public static long GetVerifiedLong(this IAppendStream s, long position) {
        var long1 = GetLong(s, position);
        var long2 = GetLong(s, position + 8);
        if (long1 != -long2) throw new Exception("Verification failed. Invalid binary data. ");
        return long1;
    }
    public static Guid GetGuid(this IAppendStream s, long position) {
        var bytes = new byte[16];
        s.Get(position, 16, bytes);
        return new Guid(bytes);
    }
    public static Guid GetGuid(this IAppendStream s, ref long position) {
        var bytes = new byte[16];
        s.Get(position, 16, bytes);
        position += 16;
        return new Guid(bytes);
    }

    //unsafe public static void WriteDecimal(this IAppendStream s, decimal v) {
    //    var buffer = new byte[16];
    //    fixed (byte* p = buffer) *(decimal*)p = v;
    //}
    //unsafe public static decimal ReadDecimal(this IReadStream s) {
    //    fixed (byte* p = s.Read(16)) return *(decimal*)p;
    //}

    public static void WriteDouble(this IAppendStream s, double v) {
        s.Append(BitConverter.GetBytes(v));
    }
    public static double ReadDouble(this IReadStream s) {
        return BitConverter.ToDouble(s.Read(8), 0);
    }

    unsafe public static void WriteDecimal(this IAppendStream s, decimal v) {
        var buffer = new byte[16];
        fixed (byte* p = buffer) *(decimal*)p = v;
        s.Append(buffer);
    }
    unsafe public static decimal ReadDecimal(this IReadStream s) {
        var buffer = s.Read(16);
        fixed (byte* p = buffer) return *(decimal*)p;
    }

    public static void WriteFloat(this IAppendStream s, float v) {
        s.Append(BitConverter.GetBytes(v));
    }
    public static float ReadFloat(this IReadStream s) {
        return BitConverter.ToSingle(s.Read(4), 0);
    }

    public static void WriteULong(this IAppendStream s, ulong v) {
        s.Append(BitConverter.GetBytes(v));
    }
    public static ulong ReadULong(this IReadStream s) {
        return BitConverter.ToUInt64(s.Read(8), 0);
    }

    public static void WriteChar(this IAppendStream s, char v) {
        s.Append(BitConverter.GetBytes(v));
    }
    public static char ReadChar(this IReadStream s) {
        return BitConverter.ToChar(s.Read(2), 0);
    }
    public static string ReadUTF8StringNoLengthPrefix(this IReadStream s, int length) { // no length prefix
        return Encoding.UTF8.GetString(s.Read(length));
    }
    public static void WriteUTF8StringNoLengthPrefix(this IAppendStream s, string v) { // no length prefix
        var bytes = Encoding.UTF8.GetBytes(v);
        s.Append(bytes);
    }
    public static void WriteString(this IAppendStream s, string v) {
        var bytes = RelatudeDBGlobals.Encoding.GetBytes(v);
        s.WriteVerifiedInt(bytes.Length);
        s.Append(bytes);
    }
    public static string ReadString(this IReadStream s) {
        var length = s.ReadVerifiedInt();
        return RelatudeDBGlobals.Encoding.GetString(s.Read(length));
    }

    public static void WriteDateTimeUtc(this IAppendStream s, DateTime v) {
        s.Append(BitConverter.GetBytes(v.Ticks));
    }
    public static DateTime ReadDateTimeUtc(this IReadStream s) {
        return new DateTime(BitConverter.ToInt64(s.Read(8), 0), DateTimeKind.Utc);
    }

    public static void WriteDateTimeOffset(this IAppendStream s, DateTimeOffset v) {
        var utcTicks = v.UtcDateTime.Ticks;
        var offsetMinutes = checked((short)v.Offset.TotalMinutes); // offsets are minute-based, range ±14h

        Span<byte> buf = stackalloc byte[10];
        BinaryPrimitives.WriteInt64LittleEndian(buf[..8], utcTicks);
        BinaryPrimitives.WriteInt16LittleEndian(buf.Slice(8, 2), offsetMinutes);
        s.Append(buf.ToArray());
    }
    public static DateTimeOffset ReadDateTimeOffset(this IReadStream s) {
        var bytes = s.Read(10);
        var utcTicks = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(..8));
        var offsetMinutes = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(8, 2));
        var offset = TimeSpan.FromMinutes(offsetMinutes);
        // Construct the same instant, but with the preserved offset
        return new DateTimeOffset(new DateTime(utcTicks, DateTimeKind.Utc)).ToOffset(offset);
    }
    public static void WriteTimeSpan(this IAppendStream s, TimeSpan v) {
        s.Append(BitConverter.GetBytes(v.Ticks));
    }
    public static TimeSpan ReadTimeSpan(this IReadStream s) {
        return new TimeSpan(BitConverter.ToInt64(s.Read(8), 0));
    }

    public static DateTime? ReadDateTimeNullable(this IReadStream s) {
        if (s.ReadBool()) {
            return s.ReadDateTimeUtc();
        } else {
            return null;
        }
    }
    public static void WriteDateTimeNullable(this IAppendStream s, DateTime? v) {
        if (v.HasValue) {
            s.WriteBool(true);
            s.WriteDateTimeUtc(v.Value);
        } else {
            s.WriteBool(false);
        }
    }

    public static void WriteGuid(this IAppendStream s, Guid v) {
        s.Append(v.ToByteArray());
    }
    public static Guid ReadGuid(this IReadStream s) {
        return new Guid(s.Read(16));
    }

    public static void WriteByteArray(this IAppendStream s, byte[] v) {
        s.WriteVerifiedInt(v.Length);
        s.Append(v);
    }
    public static byte[] ReadByteArray(this IReadStream s) {
        var length = s.ReadVerifiedInt();
        return s.Read(length);
    }
    public static void SkipByteArray(this IReadStream s) {
        var length = s.ReadVerifiedInt();
        s.Skip(length);
    }


    public static void WriteStringArray(this IAppendStream s, string[] v) {
        s.WriteVerifiedInt(v.Length);
        foreach (var str in v) s.WriteString(str);
    }
    public static string[] ReadStringArray(this IReadStream s) {
        var length = s.ReadVerifiedInt();
        var v = new string[length];
        for (var n = 0; n < length; n++) v[n] = s.ReadString();
        return v;
    }

    public static void WriteCharArray(this IAppendStream s, char[] v) {
        s.WriteVerifiedInt(v.Length);
        var buffer = new byte[v.Length * 2];
        for (var n = 0; n < v.Length; n++) {
            BitConverter.GetBytes(v[n]).CopyTo(buffer, n * 2);
        }
        s.Append(buffer);
    }
    public static char[] ReadCharArray(this IReadStream s) {
        var length = s.ReadVerifiedInt();
        var data = s.Read(length * 2);
        var r = new char[length];
        for (var n = 0; n < length; n++) {
            r[n] = BitConverter.ToChar(data, n * 2);
        }
        return r;
    }

    public static void WriteFloatArray(this IAppendStream s, float[] v) {
        s.WriteVerifiedInt(v.Length);
        var buffer = new byte[v.Length * 4];
        for (var n = 0; n < v.Length; n++) {
            BitConverter.GetBytes(v[n]).CopyTo(buffer, n * 4);
        }
        s.Append(buffer);
    }
    public static float[] ReadFloatArray(this IReadStream s) {
        var length = s.ReadVerifiedInt();
        var data = s.Read(length * 4);
        var r = new float[length];
        for (var n = 0; n < length; n++) {
            r[n] = BitConverter.ToSingle(data, n * 4);
        }
        return r;
    }

    public static void WriteIntArray(this IAppendStream s, int[] v) {
        s.WriteVerifiedInt(v.Length);
        var buffer = new byte[v.Length * 4];
        for (var n = 0; n < v.Length; n++) {
            BitConverter.GetBytes(v[n]).CopyTo(buffer, n * 4);
        }
        s.Append(buffer);
    }
    public static int[] ReadIntArray(this IReadStream s) {
        var length = s.ReadVerifiedInt();
        var data = s.Read(length * 4);
        var r = new int[length];
        for (var n = 0; n < length; n++) {
            r[n] = BitConverter.ToInt32(data, n * 4);
        }
        return r;
    }
}
