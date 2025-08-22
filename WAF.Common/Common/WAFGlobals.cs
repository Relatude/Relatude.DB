using System.Text;

public static class WAFGlobals {
    //public static Encoding Encoding = Encoding.Unicode; // Sligthly less mem but double file size on strings, similar speed unless disk is slow
    public static Encoding Encoding = Encoding.UTF8; // Slightly more mem but half file size on strings, similar speed unless disk is slow
}
