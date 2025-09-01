using Relatude.DB.Demo.Models;
namespace Relatude.DB.Demo;
public class DemoArticleGenerator(int seed = 0) {
    TextGenerator _textGenerator = new(seed);
    public DemoArticle One() {
        return new DemoArticle {
            Title = _textGenerator.GenerateTitle(50),
            Content = _textGenerator.GenerateText(2048),
        };
    }
    public DemoArticle[] Many(int count) {
        DemoArticle[] articles = new DemoArticle[count];
        for (int i = 0; i < count; i++) articles[i] = One();
        return articles;
    }
    public IEnumerable<DemoArticle> Enumerate(int count) {
        for (int i = 0; i < count; i++) {
            yield return One();
        }
    }
}
