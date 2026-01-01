using Relatude.DB.Demo.Models;
namespace Relatude.DB.Demo;
public class RandomArticleGenerator(int seed = 0) : IArticleGenerator {
    TextGenerator _textGenerator = new(seed);
    public DemoArticle One() {
        return new DemoArticle {
            Title = _textGenerator.GenerateTitle(50),
            Content = _textGenerator.GenerateText(2048),
        };
    }
    public void Move(int count) {
        _textGenerator.NewSeed(count);
        //for (int i = 0; i < count; i++) {
        //    _ = One();
        //}
    }
    public DemoArticle[] Many(int count) {
        DemoArticle[] articles = new DemoArticle[count];
        for (int i = 0; i < count; i++) articles[i] = One();
        return articles;
    }
    public void Dispose() {        
    }
}
