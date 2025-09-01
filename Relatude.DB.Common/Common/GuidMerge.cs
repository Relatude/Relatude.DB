namespace Relatude.DB.Common;
public static class GuidMerge {
    // Merge two Guids into one, the result is not commutative
    public static Guid Merge(this Guid guid1, Guid guid2) {
        if (guid1 > guid2) {
            var temp = guid1;
            guid1 = guid2;
            guid2 = temp;
        }
        byte[] a = guid1.ToByteArray();
        byte[] b = guid2.ToByteArray();
        byte[] c = new byte[16];
        for (int i = 0; i < 16; i++) {
            c[i] = (byte)(a[i] ^ b[i]);
        }
        return new Guid(c);
    }
}

