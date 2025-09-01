using System.Text;

namespace Relatude.DB.DataStores.Indexes {
    public class CssParser {
        static int findFirstSpaceOrBracketEnd(string html, int pos) {
            while (pos < html.Length) {
                var c = html[pos];
                if (c == ' ' || c == ')') return pos;
                pos++;
            }
            return -1;
        }
        static int findFirstNoneSpaceOrQuotationMark(string html, int pos, out char quotationMark, out bool hasQuotationMarks) {
            while (pos < html.Length) {
                var c = html[pos];
                if (c == '\'') { quotationMark = '\''; hasQuotationMarks = true; return pos; }
                if (c == '"') { quotationMark = '"'; hasQuotationMarks = true; return pos; }
                if (c != ' ') { quotationMark = ' '; hasQuotationMarks = false; return pos; }
                pos++;
            }
            quotationMark = ' '; hasQuotationMarks = false; return -1;
        }
        public static string ParseUrls(string css, Func<string, string> newUrl) {
            StringBuilder sb = new StringBuilder();
            int startTag = 0;
            var lastPosAdded = 0;
            var nextSearchPos = 0;
            while (nextSearchPos < css.Length && (startTag = css.IndexOf("url(", nextSearchPos)) > -1) {
                startTag += 4;
                var startUrl = findFirstNoneSpaceOrQuotationMark(css, startTag, out var quotationMark, out var hasQuotationMarks);
                if (startUrl > -1) {
                    int endUrl = -1;
                    if (hasQuotationMarks) {
                        startUrl++;
                        endUrl = css.IndexOf(quotationMark, startUrl);
                    } else {
                        endUrl = findFirstSpaceOrBracketEnd(css, startUrl);
                    }
                    if (endUrl > -1) {
                        var url = css[startUrl..endUrl];
                        string newValue = newUrl(url);
                        sb.Append(css[lastPosAdded..startUrl]);
                        sb.Append(newValue);
                        lastPosAdded = endUrl;
                        nextSearchPos = endUrl + 1;

                    } else { // no end of url
                        nextSearchPos = startTag + 1;
                    }
                } else { // no start of url
                    nextSearchPos = startTag + 1;
                }
            }
            sb.Append(css[lastPosAdded..css.Length]);
            return sb.ToString();

        }
    }
}
