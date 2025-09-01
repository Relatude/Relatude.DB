using System.Diagnostics;
using Relatude.DB.Common;

namespace Relatude.DB.IO {
    internal static class StringFormatHelper {
        public static string ToTransferString(this int count, Stopwatch sw) {
            var ms = sw.Elapsed.TotalMilliseconds;
            return count.ToByteString() + " " + (1000d * count / ms).ToByteString() + "/s " + ms.To1000N() + "ms";
        }
        public static string ToTransferString(this long count, Stopwatch sw) {
            return count.ToByteString() + " " + (1000d * count / sw.ElapsedMilliseconds).ToByteString() + "/s " + sw.ElapsedMilliseconds.To1000N() + "ms";
        }
    }
}
