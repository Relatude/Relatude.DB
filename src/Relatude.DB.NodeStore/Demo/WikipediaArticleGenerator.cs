using Relatude.DB.Demo.Models;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
namespace Relatude.DB.Demo;
public class WikipediaArticleGenerator : IArticleGenerator {
    readonly WikipediaReader _reader;
    public WikipediaArticleGenerator(string filePath) {
        _reader = new WikipediaReader(filePath);
    }
    public void Move(int count) {
        for (int i = 0; i < count; i++) {
            if (!_reader.ReadNext(out var w)) {
                throw new InvalidOperationException("No more articles");
            }
        }
    }
    public DemoArticle One() {
        if (_reader.ReadNext(out var w) && w != null) {
            return new DemoArticle {
                Title = w.title,
                Content = w.body,
            };
        }
        throw new InvalidOperationException("No more articles");
    }
    public DemoArticle[] Many(int count) {
        DemoArticle[] articles = new DemoArticle[count];
        for (int i = 0; i < count; i++) {
            if (_reader.ReadNext(out var w) && w != null) {
                articles[i] = new DemoArticle {
                    Title = w.title,
                    Content = w.body,
                };
            } else {
                throw new InvalidOperationException("No more articles");
            }
        }
        return articles;
    }
    public void Dispose() {
        _reader.Dispose();
    }
}
class WikipediaArticle {
    public string url { get; set; } = string.Empty;
    public string title { get; set; } = string.Empty;
    public string body { get; set; } = string.Empty;
}
class WikipediaReader : IDisposable {
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

