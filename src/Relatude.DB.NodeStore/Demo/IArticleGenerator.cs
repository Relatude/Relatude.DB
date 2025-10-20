using Relatude.DB.Demo.Models;
namespace Relatude.DB.Demo;
public interface IArticleGenerator: IDisposable {
    public DemoArticle One();
    void Move(int count);
    public DemoArticle[] Many(int count);
}
