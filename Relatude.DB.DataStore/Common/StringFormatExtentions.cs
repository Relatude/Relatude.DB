namespace Relatude.DB.Common;
public static class StringFormatExtentions {
    public static string ToByteString(this long byteCount) {
        string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; //Longs run out around EB
        if (byteCount == 0) return "0" + suf[0];
        long bytes = Math.Abs(byteCount);
        int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
        double num = Math.Round(bytes / Math.Pow(1024, place), 1);
        return (Math.Sign(byteCount) * num).ToString() + suf[place];
    }
    public static string ToByteString(this int byteCount) => ToByteString((long)byteCount);
    public static string ToByteString(this double byteCount) => ToByteString((long)byteCount);

    public static string To1000N(this long n) => n.ToString("N0");
    public static string To1000N(this int n) => n.ToString("N0");
    public static string To1000N(this double n) => n.ToString("N0");
    public static string To1000N(this float n) => n.ToString("N0");
}

