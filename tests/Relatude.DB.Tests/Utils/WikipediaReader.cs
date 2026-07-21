using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Tests.Utils {
    public class WikipediaArticle {
        public string url { get; set; } = string.Empty;
        public string title { get; set; } = string.Empty;
        public string body { get; set; } = string.Empty;
    }
    public class WikipediaReader : IDisposable {
        readonly StreamReader _stream;
        public WikipediaReader(string path) {
            _stream = File.OpenText(path);
        }
        public bool ReadNext([MaybeNullWhen(false)] out WikipediaArticle? w) {
            w = null;
            if (_stream.EndOfStream) {
                return false;
            }
            var l = _stream.ReadLine();
            if (l == null) return false;
            w = JsonSerializer.Deserialize<WikipediaArticle>(l);
            return true;
        }
        public void Dispose() {
            _stream.Dispose();
        }
    }
}
