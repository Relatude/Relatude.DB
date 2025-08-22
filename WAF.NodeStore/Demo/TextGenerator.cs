using System.Text;
namespace WAF.Demo;
public class TextGenerator {
    public int MaxWordsPerSentence = 20;
    public int TotalWordVariants = 10000; // Vocabulary
    public int MaxWordLength = 20;
    public char[] Chars = "qwertyuioplkjhgfdsazxcvbnm".ToCharArray();
    public static string[] LorumIpsumWords = """
    lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut 
    labore et dolore magna aliqua ut enim ad minim veniam quis nostrud exercitation ullamco 
    laboris nisi ut aliquip ex ea commodo consequat duis aute irure dolor in reprehenderit in 
    voluptate velit esse cillum dolore eu fugiat nulla pariatur excepteur sint occaecat cupidatat non 
    proident sunt in culpa qui officia deserunt mollit anim id est laborum
    """.ReplaceLineEndings(" ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    Random _random;
    public string[] Vocabulary;
    public TextGenerator(int seed = 0) {
        _random = new Random(seed);
        HashSet<string> words = [];
        var iterations = 0;
        while (words.Count < TotalWordVariants) {
            if (iterations < LorumIpsumWords.Length) { // Use Lorum Ipsum words first
                words.Add(LorumIpsumWords[iterations]);
            } else {
                //words.Add(randomWord());
                words.Add(WordGenerator.NewWord(MaxWordLength)); // Use WordGenerator to create new words
            }
            if (iterations > TotalWordVariants * 10) // Avoid infinite loop, could be improved....
                throw new Exception("Sorry, unable to generate enough unique words, increase word length or reduce number of words. ");
            iterations++;
        }
        Vocabulary = [.. words];
    }
    string randomWord() {
        var distribution = 2d;
        var max = (int)Math.Pow((double)MaxWordLength - 1, 1d / distribution);
        var wordLength = (int)Math.Ceiling(Math.Pow(_random.Next(max * 10000) / 10000d, distribution));
        var word = new char[wordLength];
        for (int i = 0; i < wordLength; i++) word[i] = Chars[_random.Next(Chars.Length)];
        return new string(word);
    }
    string nextWord() => Vocabulary[_random.Next(Vocabulary.Length)];

    public string GenerateTitle(int textLength) {
        StringBuilder sb = new();
        var firstWord = nextWord();
        sb.Append(char.ToUpper(firstWord[0]));
        sb.Append(firstWord[1..]);
        while (sb.Length < textLength) {
            sb.Append(' ');
            sb.Append(nextWord());
        }
        return sb.ToString();
    }
    public string GenerateText(int textLength) {
        StringBuilder sb = new();
        while (sb.Length < textLength) {
            var wordsInSentence = _random.Next(MaxWordsPerSentence);
            var firstWord = nextWord();
            sb.Append(char.ToUpper(firstWord[0]));
            sb.Append(firstWord[1..]);
            for (int i = 0; i < wordsInSentence - 1; i++) {
                if (sb.Length > textLength) break;
                sb.Append(' ');
                sb.Append(nextWord());
            }
            sb.Append(". ");
        }
        return sb.ToString();
    }
}
