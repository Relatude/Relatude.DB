using System.Text;

namespace Relatude.DB.Common {
    public static class StreamExtenstions {

        public static void WriteBool(this Stream s, bool v) {
            s.WriteByte((byte)(v ? 1 : 0)); // I know, wasting space.... but negligible in the larger context....
        }
        public static bool ReadBool(this Stream s) {
            return s.ReadByte() == 1;
        }

        public static void WriteOneByte(this Stream s, byte v) {
            s.WriteByte(v);
        }
        public static byte ReadOneByte(this Stream s) {
            byte[] b = new byte[1];
            s.Read(b, 0, 1);
            return b[0];
        }

        public static void WriteUInt(this Stream s, uint v) {
            s.Write(BitConverter.GetBytes(v), 0, 4);
        }
        public static uint ReadUInt(this Stream s) {
            byte[] b = new byte[4];
            s.Read(b, 0, 4);
            return BitConverter.ToUInt32(b, 0);
        }

        public static void WriteInt(this Stream s, int v) {
            s.Write(BitConverter.GetBytes(v), 0, 4);
        }
        public static int ReadInt(this Stream s) {
            byte[] b = new byte[4];
            s.Read(b, 0, 4);
            return BitConverter.ToInt32(b, 0);
        }
        public static void WriteLong(this Stream s, long v) {
            s.Write(BitConverter.GetBytes(v), 0, 8);
        }
        public static long ReadLong(this Stream s) {
            byte[] b = new byte[8];
            s.Read(b, 0, 8);
            return BitConverter.ToInt64(b, 0);
        }

        public static void WriteDateTime(this Stream s, DateTime v) {
            s.Write(BitConverter.GetBytes(v.Ticks), 0, 8);
        }
        public static DateTime ReadDateTime(this Stream s) {
            byte[] b = new byte[8];
            s.Read(b, 0, 8);
            return new DateTime(BitConverter.ToInt64(b, 0));
        }
        public static DateTime? ReadDateTimeOrNull(this Stream s) {
            if (s.ReadBool()) {
                return s.ReadDateTime();
            } else {
                return null;
            }
        }
        public static void WriteDateTimeOrNull(this Stream s, DateTime? v) {
            if (v.HasValue) {
                s.WriteBool(true);
                s.WriteDateTime(v.Value);
            } else {
                s.WriteBool(false);
            }
        }

        public static void WriteDateTimeOffset(this Stream s, DateTimeOffset v) {
            s.Write(BitConverter.GetBytes(v.Ticks), 0, 8);
            s.Write(BitConverter.GetBytes(v.Offset.Ticks), 0, 8);
        }
        public static DateTimeOffset ReadDateTimeOffset(this Stream s) {
            byte[] b = new byte[8];
            s.Read(b, 0, 8);
            var ticks = BitConverter.ToInt64(b, 0);
            s.Read(b, 0, 8);
            var offsetTicks = BitConverter.ToInt64(b, 0);
            return new DateTimeOffset(new DateTime(ticks), new TimeSpan(offsetTicks));
        }

        public static void WriteTimeSpan(this Stream s, TimeSpan v) {
            s.Write(BitConverter.GetBytes(v.Ticks), 0, 8);
        }
        public static TimeSpan ReadTimeSpan(this Stream s) {
            byte[] b = new byte[8];
            s.Read(b, 0, 8);
            return new TimeSpan(BitConverter.ToInt64(b, 0));
        }
        public static TimeSpan? ReadTimeSpanOrNull(this Stream s) {
            if (s.ReadBool()) {
                return s.ReadTimeSpan();
            } else {
                return null;
            }
        }
        public static void WriteTimeSpanOrNull(this Stream s, TimeSpan? v) {
            if (v.HasValue) {
                s.WriteBool(true);
                s.WriteTimeSpan(v.Value);
            } else {
                s.WriteBool(false);
            }
        }

        unsafe public static void WriteDecimal(this Stream s, decimal v) {
            var buffer = new byte[16];
            fixed (byte* p = buffer) *(decimal*)p = v;
            s.Write(buffer, 0, 16);
        }
        unsafe public static decimal ReadDecimal(this Stream s) {
            var buffer = new byte[16];
            s.Read(buffer, 0, 16);
            fixed (byte* p = buffer) return *(decimal*)p;
        }

        public static void WriteDouble(this Stream s, double v) {
            s.Write(BitConverter.GetBytes(v), 0, 8);
        }
        public static double ReadDouble(this Stream s) {
            byte[] b = new byte[8];
            s.Read(b, 0, 8);
            return BitConverter.ToDouble(b, 0);
        }
        public static void WriteFloat(this Stream s, float v) {
            s.Write(BitConverter.GetBytes(v), 0, 2);
        }
        public static float ReadFloat(this Stream s) {
            byte[] b = new byte[2];
            s.Read(b, 0, 2);
            return BitConverter.ToSingle(b, 0);
        }

        public static void WriteChar(this Stream s, char v) {
            s.Write(BitConverter.GetBytes(v), 0, 2);
        }
        public static char ReadChar(this Stream s) {
            byte[] b = new byte[2];
            s.Read(b, 0, 2);
            return BitConverter.ToChar(b, 0);
        }

        public static void WriteULong(this Stream s, ulong v) {
            s.Write(BitConverter.GetBytes(v), 0, 8);
        }
        public static ulong ReadULong(this Stream s) {
            byte[] b = new byte[8];
            s.Read(b, 0, 8);
            return BitConverter.ToUInt64(b, 0);
        }

        public static void WriteString(this Stream s, string v) {
            var bytes = RelatudeDBGlobals.Encoding.GetBytes(v);
            s.WriteInt(bytes.Length);
            s.Write(bytes, 0, bytes.Length);
        }
        public static string ReadString(this Stream s) {
            var length = s.ReadInt();
            var bs = new byte[length];
            s.Read(bs, 0, length);
            return RelatudeDBGlobals.Encoding.GetString(bs);
        }
        public static void WriteStringOrNull(this Stream s, string? v) {
            if (v == null) {
                s.WriteBool(false);
            } else {
                s.WriteBool(true);
                s.WriteString(v);
            }
        }
        public static string? ReadStringOrNull(this Stream s) {
            if (s.ReadBool()) {
                return s.ReadString();
            } else {
                return null;
            }
        }

        public static void WriteGuid(this Stream s, Guid v) {
            s.Write(v.ToByteArray(), 0, 16);
        }
        public static Guid ReadGuid(this Stream s) {
            byte[] b = new byte[16];
            s.Read(b, 0, 16);
            return new Guid(b);
        }
        public static void WriteVerifiedByteArray(this Stream s, byte[] v) {
            s.WriteInt(v.Length);
            s.WriteInt(v.Length);
            s.Write(v, 0, v.Length);
        }
        public static void WriteByteArray(this Stream s, byte[] v) {
            s.WriteInt(v.Length);
            s.Write(v, 0, v.Length);
        }
        public static byte[] ReadByteArray(this Stream s) {
            var length = s.ReadInt();
            var bs = new byte[length];
            s.Read(bs, 0, length);
            return bs;
        }
        public static byte[] ReadVerifiedByteArray(this Stream s) {
            var length = s.ReadInt();
            var lengthCheck = s.ReadInt();
            if (length != lengthCheck) throw new InvalidDataException("Byte array length verification failed. ");
            var bs = new byte[length];
            s.Read(bs, 0, length);
            return bs;
        }
        public static void WriteFloatArray(this Stream s, float[] v) {
            s.WriteInt(v.Length);
            var buffer = new byte[v.Length * 4];
            for (var n = 0; n < v.Length; n++) {
                BitConverter.GetBytes(v[n]).CopyTo(buffer, n * 4);
            }
            s.Write(buffer, 0, buffer.Length);
        }
        public static float[] ReadFloatArray(this Stream s) {
            var length = s.ReadInt();
            var buffer = new byte[length * 4];
            s.Read(buffer, 0, length * 4);
            var r = new float[length];
            for (var n = 0; n < length; n++) {
                r[n] = BitConverter.ToSingle(buffer, n * 4);
            }
            return r;
        }

        public static void WriteStringArray(this Stream s, string[] v) {
            s.WriteInt(v.Length);
            foreach (var str in v) s.WriteString(str);
        }
        public static string[] ReadStringArray(this Stream s) {
            var length = s.ReadInt();
            var v = new string[length];
            for (var n = 0; n < length; n++) v[n] = s.ReadString();
            return v;
        }

        public static void WriteCharArray(this Stream s, char[] v) {
            s.WriteInt(v.Length);
            var buffer = new byte[v.Length * 2];
            for (var n = 0; n < v.Length; n++) {
                BitConverter.GetBytes(v[n]).CopyTo(buffer, n * 2);
            }
            s.Write(buffer, 0, buffer.Length);
        }
        public static char[] ReadCharArray(this Stream s) {
            var length = s.ReadInt();
            var buffer = new byte[length * 2];
            s.Read(buffer, 0, length * 2);
            var r = new char[length];
            for (var n = 0; n < length; n++) {
                r[n] = BitConverter.ToChar(buffer, n * 2);
            }
            return r;
        }

    }
}

