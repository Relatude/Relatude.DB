using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tests.Utils
{
    public class TextGenerator
    {
        public int MaxWordsPerSentence = 30;
        public int TotalWordVariants = 10000; // Vocabulary
        public int MaxWordLength = 25;
        public char[] Chars = "qwertyuioplkjhgfdsazxcvbnm".ToCharArray();
        Random _random;
        string[] _dictionary;

        public TextGenerator(int seed = 0)
        {
            _random = new Random(seed);
            HashSet<string> words = new HashSet<string>();
            var iterations = 0;
            while (words.Count < TotalWordVariants)
            {
                words.Add(createNewWord());
                if (iterations++ > TotalWordVariants * 10)
                    throw new Exception("Sorry, unable to generate enough unique words, increase word length or reduce number of words. ");
            }
            _dictionary = words.ToArray();
        }
        public string createNewWord()
        {
            var distribution = 2d;
            var max = (int)Math.Pow((double)MaxWordLength - 1, 1d / distribution);
            var wordLength = (int)Math.Ceiling(Math.Pow(_random.Next(max * 10000) / 10000d, distribution));
            var word = new char[wordLength];
            for (int i = 0; i < wordLength; i++)
            {
                word[i] = Chars[_random.Next(Chars.Length)];
            }
            return new string(word);
        }
        string nextWord()
        {
            return _dictionary[_random.Next(_dictionary.Length)];
        }
        public string GenerateText(int textLength)
        {
            StringBuilder sb = new();
            while (sb.Length < textLength)
            {
                var wordsInSentence = _random.Next(MaxWordsPerSentence);
                var firstWord = nextWord();
                sb.Append(char.ToUpper(firstWord[0]));
                sb.Append(firstWord[1..]);
                for (int i = 0; i < wordsInSentence - 1; i++)
                {
                    if (sb.Length > textLength) break;
                    sb.Append(' ');
                    sb.Append(nextWord());
                }
                sb.Append(". ");
            }
            return sb.ToString();
        }
    }
}
