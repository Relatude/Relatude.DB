using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.DB.Common;

public static class B64 {
    public static string EncodeForUrl(byte[] bytes) {
        int unpaddedLen = (bytes.Length * 4 + 2) / 3;
        Span<char> buf = stackalloc char[(unpaddedLen + 3) & ~3];
        Convert.TryToBase64Chars(bytes, buf, out _);
        for (int i = 0; i < unpaddedLen; i++) { if (buf[i] == '+') buf[i] = '-'; else if (buf[i] == '/') buf[i] = '_'; }
        return new string(buf[..unpaddedLen]);
    }
    public static bool TryDecodeFromUrlParameter(string s, out byte[] result) {
        int paddedLen = (s.Length + 3) & ~3;
        Span<char> buf = stackalloc char[paddedLen];
        for (int i = 0; i < s.Length; i++) { var c = s[i]; buf[i] = c == '-' ? '+' : c == '_' ? '/' : c; }
        buf[s.Length..].Fill('=');
        result = new byte[paddedLen / 4 * 3];
        if (!Convert.TryFromBase64Chars(buf, result, out int written)) { result = []; return false; }
        result = result[..written]; return true;
    }
}
