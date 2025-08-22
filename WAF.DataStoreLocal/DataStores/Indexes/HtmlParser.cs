using System.Text;
using System.Web;

namespace WAF.DataStores.Indexes {
    public struct Favicon {
        public string Src;
        public string Sizes;
        public string Type;
    }
    // Utility for identifying and modifying url references in HTML documents
    // Identifies urls of links and images with methods: ".Links", ".Images", ".Urls"
    // Allows you to modify urls with: ".ChangeLinks", ".ChangeUrls", ".ChangeImages"
    // Allows you to modify any tag and attribute with ".Change"
    // It has been designed for speed and simplicity, and will leave the html exactly as it was (apart from changes you make)
    // It does not evaluate or consider the DOM tree, it simply parse the document as flat list of tags and text
    public delegate string TagFunc(string html, string tagName, Dictionary<string, string>? attributes);
    public delegate string? AttrFunc(string tagName, string attrName, string value);
    public static class HtmlParser {
        public static List<string> Links(string html) {
            var urls = new List<string>();
            var h = parse(html,
                   (html, tagName, attributes) => html,
                   (tagName, attrName, value) => {
                       if (tagName != "link" && attrName == "href") urls.Add(value);
                       return null;
                   }
               , false);
            return urls;
        }
        public static List<string?> Images(string html) {
            var urls = new List<string?>();
            var h = parse(html,
                   (html, tagName, attributes) => html,
                   (tagName, attrName, value) => {
                       switch (attrName) {
                           case "src":
                           case "data-src":
                               if (tagName == "img") urls.Add(value);
                               break;
                           case "srcset":
                               foreach (var s in value.Split(',')) urls.Add(s.Split(' ')[0].Trim()); // removing x-descriptors
                               break;
                           case "style":
                               CssParser.ParseUrls(value, (v) => { urls.Add(v); return v; });
                               break;
                           default:
                               break;
                       }
                       return null;
                   }
               , false);
            return urls;
        }
        public static List<Favicon> Favicons(string html) {
            var urls = new List<Favicon>();
            var h = parse(html,
                   (html, tagName, attributes) => {
                       if (tagName == "link" && attributes != null && attributes.TryGetValue("rel", out var rel) && rel.Contains("icon")) {
                           if (attributes.TryGetValue("href", out var href)) {
                               var favicon = new Favicon() { Src = href };
                               if (attributes.TryGetValue("sizes", out var sizes)) favicon.Sizes = sizes;
                               if (attributes.TryGetValue("type", out var type)) favicon.Type = type;
                               urls.Add(favicon);
                           }
                       }
                       return html;
                   },
                   (tagName, attrName, value) => value
               , true);
            return urls;
        }
        public static List<string> Urls(string html) {
            var urls = new List<string>();
            var h = parse(html,
                   (html, tagName, attributes) => html,
                   (tagName, attrName, value) => {
                       switch (attrName) {
                           case "src":
                           case "data-src":
                           case "href":
                               urls.Add(value);
                               break;
                           case "srcset":
                               foreach (var s in value.Split(',')) urls.Add(s.Split(' ')[0].Trim()); // removing x-descriptors
                               break;
                           case "style":
                               CssParser.ParseUrls(value, (v) => { urls.Add(v); return v; });
                               break;
                           default:
                               break;
                       }
                       return null;
                   }
               , false);
            return urls;
        }
        public static string ChangeLinks(string html, Func<string, string> newUrl) {
            return Change(html,
                (html, tagName, attributes) => html,
                (tagName, attrName, value) => tagName != "link" && attrName == "href" ? newUrl(value) : value
            );
        }
        public static string ChangeUrls(string html, Func<string, string> newUrl) {
            return Change(html,
                (html, tagName, attributes) => html,
                (tagName, attrName, value) => {
                    switch (attrName) {
                        case "src":
                        case "data-src":
                        case "href":
                            return newUrl(value);
                        case "srcset": {
                                var sets = value.Split(',');
                                if (sets.Length == 1) return newUrl(value);
                                // dealing with x-descriptors:
                                var result = new StringBuilder();
                                foreach (var s in sets) {
                                    var parts = s.Trim().Split(' ');
                                    if (parts.Length == 2) {
                                        result.Append(newUrl(parts[0].Trim()));
                                        result.Append(' ');
                                        result.Append(parts[1].Trim());
                                    } else {
                                        result.Append(newUrl(s));
                                    }
                                    result.Append(", ");
                                }
                                return result.Length > 2 ? result.ToString().Substring(0, result.Length - 2) : result.ToString();
                            }
                        case "style":
                            return CssParser.ParseUrls(value, newUrl);
                        default:
                            return value;
                    }
                }
            );
        }
        public static string ChangeImages(string html, Func<string, string> newUrl) {
            return Change(html,
                (html, tagName, attributes) => html,
                (tagName, attrName, value) => {
                    switch (attrName) {
                        case "src":
                        case "data-src":
                            return tagName == "img" ? newUrl(value) : value;
                        case "srcset": {
                                var sets = value.Split(',');
                                if (sets.Length == 1) return newUrl(value);
                                // dealing with x-descriptors:
                                var result = new StringBuilder();
                                foreach (var s in sets) {
                                    var parts = s.Trim().Split(' ');
                                    if (parts.Length == 2) {
                                        result.Append(newUrl(parts[0].Trim()));
                                        result.Append(' ');
                                        result.Append(parts[1].Trim());
                                    } else {
                                        result.Append(newUrl(s));
                                    }
                                    result.Append(", ");
                                }
                                return result.Length > 2 ? result.ToString().Substring(0, result.Length - 2) : result.ToString();
                            }
                        case "style":
                            return CssParser.ParseUrls(value, newUrl);
                        default:
                            return value;
                    }
                }
            );
        }
        public static string Change(string html, TagFunc changeTag, AttrFunc changeAttribute) {
            return parse(html, changeTag, changeAttribute, true);
        }
        static bool omitTags(string tagName) {
            return tagName == "script" || tagName == "style" || tagName == "!--" || tagName == "textarea";
        }
        public static string ToText(string html) {
            var text = new StringBuilder();
            int p = 0;
            string tagName = string.Empty;
            int lastPos = -1;
            while (p < html.Length) {
                if (p == lastPos) throw new Exception("Invalid HTML or parser error."); // just a safety to prevent infinite loops due to bugs
                lastPos = p;
                var tagStart = findTagStart(html, p, ref tagName);
                if (tagStart == -1) break;
                if (p < tagStart) text.Append(extractText(html[p..tagStart]));
                tagName = tagName.ToLower();
                if (omitTags(tagName)) {
                    var startClosingTag = findStartOfClosingTag(html, tagStart, tagName);
                    if (startClosingTag == -1)
                        break;
                    var endOfClosing = findTagEnd(html, startClosingTag);
                    if (endOfClosing == -1)
                        break;
                    p = endOfClosing + 1;
                } else {
                    var tagEnd = findTagEnd(html, tagStart);
                    if (tagEnd == -1) break;
                    p = tagEnd + 1;
                }
            }
            return text.ToString();
        }
        static string extractText(string content) {
            content = HttpUtility.HtmlDecode(content).Trim();
            var isText = true;
            if (content.Length > 3000) { // simple test if content likely is text ( sometimes pages use divs for data )
                var sample = content.Substring(0, 3000).Trim();
                var posSpace = sample.IndexOf(' ');
                if (posSpace == -1 || sample.Length - posSpace > 1000) {
                    isText = false;
                } else {
                    var words = sample.Split(' ');
                    if (words.Length < 10 || words.Select(w => w.Length).Max() > 200) {
                        isText = false;
                    }
                }
            }
            if (isText) {
                content = content.Trim();
                if (content.Length > 0) return content + " ";
            }
            return string.Empty;
        }
        static string parse(string html, TagFunc tagCallback, AttrFunc? attributeCallback, bool hasOutput) {
            int p = 0;
            var doc = new StringBuilder();
            var tag = new StringBuilder();
            string tagName = string.Empty;
            int lastPos = -1;
            while (p < html.Length) {
            startOfParseLoop:
                if (p == lastPos) throw new Exception("Invalid HTML or parser error.");
                lastPos = p;
                var tagStart = findTagStart(html, p, ref tagName);
                if (tagStart == -1) break;
                if (hasOutput) doc.Append(html[p..tagStart]); // text until tag start
                p = tagStart;
                if (omitTags(tagName)) {
                    var startOfClosingTag = findStartOfClosingTag(html, tagStart + tagName.Length, tagName);
                    if (startOfClosingTag != -1) {
                        var endOfClosing = findTagEnd(html, startOfClosingTag);
                        if (endOfClosing != -1) {
                            if (hasOutput) doc.Append(html[p..endOfClosing]); // text until end of closing tag
                            p = endOfClosing;
                            goto startOfParseLoop;
                        }
                    }
                    // if no closing tag was found it continue parsing as normal to avoid omitting whole doc
                }
                tag.Clear();
                var tagEnd = parseUntilTagEnd(html, tag, tagName, tagStart, out var attributes, attributeCallback, hasOutput);
                if (tagEnd == -1) break;
                if (hasOutput) doc.Append(tagCallback(tag.ToString(), tagName, attributes));
                p = tagEnd;
            }
            if (hasOutput && p < html.Length) doc.Append(html[p..html.Length]);
            return doc.ToString();
        }
        static int findStartOfClosingTag(string html, int startTagEnd, string tagName) {
            if (startTagEnd + tagName.Length + 3 > html.Length) return -1;
            if (tagName == "!--") {
                var start = startTagEnd;
            startOfCommentSearch:
                start = html.IndexOf('-', start + 4);
                if (start == -1) return -1;
                if (html[start + 1] != '-') goto startOfCommentSearch;
                if (html[start + 2] != '>') goto startOfCommentSearch;
                return start;
            } else {
                var start = startTagEnd;
            startOfSearch:
                start = html.IndexOf('<', start + tagName.Length + 2);
                if (start == -1) return -1;
                if (html[start + 1] != '/') goto startOfSearch;
                for (int n = 0; n < tagName.Length; n++) if (html[start + 2 + n] != tagName[n]) goto startOfSearch;
                if (html[start + 2 + tagName.Length] != '>') goto startOfSearch;
                return start;
            }
        }
        static int findTagEnd(string html, int tagStart) {
            return html.IndexOf('>', tagStart);  // assumes proper encoding
        }
        static int parseUntilTagEnd(string html, StringBuilder tag, string tagName, int tagStart, out Dictionary<string, string>? attributes, AttrFunc? attributeCallback, bool hasOutput) {
            var endOfTag = findTagEnd(html, tagStart);
            attributes = default;
            if (endOfTag == -1) return -1; // no tag left
            var tagNameEnd = tagStart + tagName.Length + 1;
            if (hasOutput) tag.Append(html[tagStart..tagNameEnd]);
            var p = tagNameEnd;
            var lastp = -1;
            while (p < endOfTag) {
                if (p == lastp) throw new Exception("Invalid HTML or parser error.");
                lastp = p;
                var nameStart = findAttrNameStartOrTagEnd(html, p, endOfTag, out var isEndOfTag);
                if (isEndOfTag) break;
                if (nameStart == -1) return -1;
                var nameEnd = findAttrNameEnd(html, nameStart, endOfTag);
                if (nameEnd == -1) return -1;
                var attrName = html[nameStart..nameEnd];
                if (attributes == default) attributes = new Dictionary<string, string>();
                var hasValue = doesAttrHaveValue(html, nameEnd, endOfTag, out var posAfterEqualChar);
                if (hasValue && posAfterEqualChar == -1) return -1;
                if (hasValue) {
                    var valueStart = findAttrValueStart(html, posAfterEqualChar, endOfTag, out var quoteChar, out var hasQuotes);
                    if (valueStart == -1) return -1;
                    var valueEnd = findAttrValueEnd(html, valueStart, endOfTag, quoteChar, hasQuotes);
                    if (valueEnd == -1) return -1;
                    string? value = html[valueStart..valueEnd];
                    if (hasOutput) tag.Append(html[p..valueStart]);
                    if (value != null && attributeCallback != null) value = attributeCallback(tagName, attrName, value);
                    if (hasOutput) {
                        tag.Append(value);
                        if (hasQuotes) tag.Append(quoteChar);
                    }
                    if (value != null) attributes[attrName] = value;
                    p = valueEnd + (hasQuotes ? 1 : 0);
                } else {
                    //attrName = attributeCallback(attrName, tagName, attrName);
                    attributes[attrName] = attrName;
                    if (hasOutput) tag.Append(html[p..nameEnd]);
                    p = nameEnd;
                }
            }
            if (hasOutput) tag.Append(html[p..(endOfTag + 1)]);
            return endOfTag + 1;
        }
        static int findTagStart(string html, int p, ref string tagName) {
            var start = html.IndexOf('<', p);
            if (start == -1 || html.Length - start < 5) return -1; // no tag left
            p = start + 1;
            while (++p < html.Length || p - start > 14) { // 14 is max length tag name
                var c = html[p];
                if (char.IsWhiteSpace(c) && p - start > 1 || c == '>') {
                    tagName = html[(start + 1)..p];
                    return start;
                } else if (!(char.IsLetterOrDigit(c) || c == '/' || c == '-' || c == '!')) { // invalid char, so look for next tag. only interested in tags with letters in their name
                    start = html.IndexOf('<', p);
                    if (start == -1 || html.Length - start < 5) return -1; // no tag left
                    p = start + 1;
                }
            }
            return -1; // no tag found
        }
        static int findAttrNameStartOrTagEnd(string html, int pos, int endOfTag, out bool isEndOfTag) {
            isEndOfTag = false;
            while (pos < endOfTag) {
                var c = html[pos];
                if (!char.IsWhiteSpace(c)) {
                    if (c == '/') {
                        isEndOfTag = true;
                        return -1;
                    }
                    if (char.IsLetterOrDigit(html[pos])) return pos;
                }
                pos++;
            }
            isEndOfTag = true;
            return -1;
        }
        static int findAttrNameEnd(string html, int pos, int endOfTag) {
            while (pos < endOfTag) {
                var c = html[pos];
                if (c == '=' || char.IsWhiteSpace(c) || c == '/') return pos;
                pos++;
            }
            if (pos < html.Length && html[pos] == '>') return pos;
            return -1;
        }
        static int findAttrValueStart(string html, int pos, int endOfTag, out char quoteChar, out bool hasQuotes) {
            while (pos < endOfTag) {
                var c = html[pos];
                if (c == '\'' || c == '"') {
                    hasQuotes = true;
                    quoteChar = c;
                    return pos + 1;
                } else if (char.IsLetterOrDigit(c)) {
                    hasQuotes = false;
                    quoteChar = default;
                    return pos;
                }
                pos++;
            }
            hasQuotes = default;
            quoteChar = default;
            return -1;
        }
        static bool doesAttrHaveValue(string html, int pos, int endOfTag, out int posAfterEqualChar) {
            while (pos < endOfTag) {
                var c = html[pos];
                if (c == '=') {
                    posAfterEqualChar = pos + 1;
                    return true;
                } else if (!char.IsWhiteSpace(c)) {  //  char.IsLetterOrDigit(c)
                    posAfterEqualChar = pos;
                    return false;
                }
                pos++;
            }
            posAfterEqualChar = -1;
            return default;
        }
        static int findAttrValueEnd(string html, int pos, int endOfTag, char qouteChar, bool hasQuotes) {
            if (hasQuotes) {
                while (pos < endOfTag) {
                    if (html[pos] == qouteChar) return pos;
                    pos++;
                }
            } else {
                while (pos < endOfTag) {
                    var c = html[pos];
                    if (char.IsWhiteSpace(c)) return pos;
                    pos++;
                }
                if (pos < html.Length && html[pos] == '>') return pos;
            }
            return -1;
        }
    }
}
