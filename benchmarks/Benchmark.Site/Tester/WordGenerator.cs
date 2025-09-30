
namespace Benchmark.Base.ContentGeneration;
public static class WordGenerator {
    private static readonly Random _random = new Random();

    // Common English consonants and vowels, prioritized for pronounceability
    private static readonly string[] Consonants = {
        "b", "c", "d", "f", "g", "h", "j", "k", "l", "m",
        "n", "p", "qu", "r", "s", "t", "v", "w", "x", "y", "z",
        "ch", "sh", "th", "ph", "wh", "tr", "dr", "bl", "cl", "gr"
    };

    private static readonly string[] Vowels = {
        "a", "e", "i", "o", "u",
        "ai", "ea", "ie", "oa", "ou", "oo"
    };

    /// <summary>
    /// Generates a new pronounceable word with maxLength, biased toward shorter lengths.
    /// </summary>
    public static string NewWord(int maxLength) {
        if (maxLength < 2) maxLength = 2;

        // Shorter lengths are more common (biased)
        int length = BiasedLength(maxLength);

        var word = "";
        bool startWithConsonant = _random.NextDouble() > 0.3;

        while (word.Length < length) {
            string nextPart = startWithConsonant
                ? Consonants[_random.Next(Consonants.Length)]
                : Vowels[_random.Next(Vowels.Length)];

            if (word.Length + nextPart.Length > length)
                break;

            word += nextPart;
            startWithConsonant = !startWithConsonant;
        }

        return (word);
    }

    private static int BiasedLength(int maxLength) {
        // Use a geometric-like distribution to favor shorter words
        double bias = 0.5; // Smaller bias = shorter words more frequent
        double r = _random.NextDouble();
        int length = (int)(Math.Log(1 - r) / Math.Log(1 - bias)) + 2;
        return Math.Min(length, maxLength);
    }

}
