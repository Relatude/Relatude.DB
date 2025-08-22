using System.Text;
namespace WAF.Common;
public class FragmentSample(bool isMatch, string fragment) {
    public bool IsMatch { get; } = isMatch;
    public string Fragment { get; set; } = fragment;
    public override string ToString() => IsMatch ? "[" + Fragment + "]" : Fragment;
}
public class TextSample {
    public FragmentSample[] Fragments { get; }
    public bool CutAtStart { get; }
    public bool CutAtEnd { get; }
    static int indexOfLevenshtein(string text, string term, int maxDistance) {
        if (string.IsNullOrEmpty(term) || string.IsNullOrEmpty(text) || term.Length > text.Length)
            return -1;

        string lowerText = text.ToLowerInvariant();
        string lowerTerm = term.ToLowerInvariant();

        int termLength = lowerTerm.Length;

        for (int i = 0; i <= lowerText.Length - termLength; i++) {
            string substring = lowerText.Substring(i, termLength);
            int distance = levenshteinDistance(substring, lowerTerm, maxDistance);
            if (distance <= maxDistance)
                return i;
        }

        return -1;
    }
    private static int levenshteinDistance(string s, string t, int maxDistance) {
        int n = s.Length;
        int m = t.Length;

        if (Math.Abs(n - m) > maxDistance)
            return maxDistance + 1;

        int[] prev = new int[m + 1];
        int[] curr = new int[m + 1];

        for (int j = 0; j <= m; j++)
            prev[j] = j;

        for (int i = 1; i <= n; i++) {
            curr[0] = i;
            int min = curr[0];

            for (int j = 1; j <= m; j++) {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(
                    curr[j - 1] + 1,     // Insertion
                    prev[j] + 1),        // Deletion
                    prev[j - 1] + cost); // Substitution

                min = Math.Min(min, curr[j]);
            }

            if (min > maxDistance)
                return maxDistance + 1;

            // Swap arrays
            var temp = prev;
            prev = curr;
            curr = temp;
        }

        return prev[m];
    }
    public TextSample(TermSet termSet, string text, int sampleLength = 255) {

        if (sampleLength > text.Length) sampleLength = text.Length;

        // first try simple but fast match:
        int matchStart = -1;
        string matchWord = string.Empty;
        foreach (var term in termSet.Terms) {
            matchStart = text.IndexOf(term.Word, StringComparison.OrdinalIgnoreCase);
            if (matchStart > -1) {
                var length = Math.Min(term.Word.Length, text.Length - matchStart);
                matchWord = text.Substring(matchStart, length);
                break;
            }
        }
        if (matchStart == -1) {
            // slow but more accurate match, including fuzzy matches:
            foreach (var term in termSet.Terms) {
                if (term.Fuzzy) {
                    DefaultLevenshtein.GetDefaultSearchDistance(term.Word.Length, out var distance1, out var distance2);
                    matchStart = indexOfLevenshtein(text, term.Word, distance1);
                    if (matchStart == -1) matchStart = indexOfLevenshtein(text, term.Word, distance2);
                    if (matchStart > -1) {
                        var length = Math.Min(term.Word.Length, text.Length - matchStart);
                        matchWord = text.Substring(matchStart, length);
                        break;
                    }
                }
            }
        }
        if (matchStart == -1) {
            Fragments = [new(false, text[..sampleLength])];
            CutAtStart = false;
            CutAtEnd = sampleLength < text.Length;
            return;
        }
        var matchLength = matchWord.Length;
        var matchEnd = matchStart + matchLength;
        if (matchStart > 0 && matchEnd < text.Length) { // match in the middle
            Fragments = new FragmentSample[3];
            var startSampleAt = (matchStart + matchLength / 2) - (sampleLength) / 2;
            if (startSampleAt < 0) startSampleAt = 0;
            var endSampleAt = startSampleAt + sampleLength;
            if (endSampleAt > text.Length) {
                if (startSampleAt > 0) startSampleAt = startSampleAt - (endSampleAt - text.Length) - matchLength;
                if (startSampleAt < 0) startSampleAt = 0;
                endSampleAt = text.Length;
            }
            CutAtStart = startSampleAt > 0;
            CutAtEnd = endSampleAt < text.Length;
            Fragments = new FragmentSample[3];
            Fragments[0] = new(false, text[startSampleAt..matchStart]);
            Fragments[1] = new(true, matchWord);
            Fragments[2] = new(false, text[matchEnd..endSampleAt]);
        } else if (matchStart > 0) { // match at the start
            CutAtStart = true;
            CutAtEnd = false;
            Fragments = new FragmentSample[2];
            var lengthOfTextBefore = sampleLength - matchLength;
            if (lengthOfTextBefore < 0) lengthOfTextBefore = 0;
            Fragments[0] = new(false, text.Substring(matchStart - lengthOfTextBefore, lengthOfTextBefore));
            Fragments[1] = new(true, matchWord);
        } else if (matchEnd < text.Length) { // match at the end
            CutAtStart = false;
            CutAtEnd = true;
            Fragments = new FragmentSample[2];
            var lengthOfTextAfter = sampleLength - matchLength;
            if (lengthOfTextAfter < 0) lengthOfTextAfter = 0;
            Fragments[0] = new(true, matchWord);
            Fragments[1] = new(false, text.Substring(matchEnd, lengthOfTextAfter));
        } else { // match at the start and end
            CutAtStart = false;
            CutAtEnd = false;
            Fragments = new FragmentSample[1];
            Fragments[0] = new(true, text.Substring(0, sampleLength));
        }
    }
    public string FormatSample(string startTag, string endTag, string startEllipse = "...", string EndEllipse = "...") {
        var sb = new StringBuilder();
        if (CutAtStart) sb.Append(startEllipse);
        foreach (var s in Fragments) {
            if (sb.Length > 0) sb.Append(" ");
            if (s.IsMatch) sb.Append(startTag);
            sb.Append(s.Fragment);
            if (s.IsMatch) sb.Append(endTag);
        }
        if (CutAtEnd) sb.Append(EndEllipse);
        return sb.ToString();
    }
    public override string ToString() => FormatSample("[", "]");
}