namespace Relatude.DB.DataStores.Indexes.TextIndexing;

// LEB128 unsigned varints, used throughout the segment file format
internal static class VarInt {
    public static void Write(Stream s, ulong v) {
        while (v >= 0x80) {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
    }
    public static void Write(Stream s, long v) => Write(s, (ulong)v);
    public static void Write(Stream s, int v) => Write(s, (ulong)(uint)v);
    public static ulong ReadULong(byte[] b, ref int pos) {
        ulong v = 0;
        var shift = 0;
        while (true) {
            var x = b[pos++];
            v |= (ulong)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return v;
            shift += 7;
        }
    }
    public static long ReadLong(byte[] b, ref int pos) => (long)ReadULong(b, ref pos);
    public static int ReadInt(byte[] b, ref int pos) => (int)(uint)ReadULong(b, ref pos);
}
