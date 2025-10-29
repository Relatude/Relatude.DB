
using Relatude.DB.Common;
using Relatude.DB.Nodes;
using System.Text.Json.Serialization;
namespace Relatude.DB.Demo.Models;

public enum DemoArticleType {
    Article,
    News,
    Blog,
    Tutorial
}
public class DemoArticle {
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Size { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public FileValue File { get; set; } = FileValue.Empty;
    public Tree.Parent Parent { get; set; } = new();
    public Tree.Children Children { get; set; } = new();
    public DemoArticleType ArticleType { get; set; } = DemoArticleType.Article;

    [RelationProperty<OneDemoArticleManyDemoArticleChildren>(RightToLeft = false)]
    public DemoArticleChild? Child { get; set; }
}

public sealed class OneDemoArticleManyDemoArticleChildren : OneToOne<DemoArticle, DemoArticleChild>;

public sealed class DemoArticleChild {
    public Guid Id { get; set; }
    public DateTimeOffset DateOfBirth3 { get; set; }
    [RelationProperty<OneDemoArticleManyDemoArticleChildren>(RightToLeft = true)]
    public DemoArticle? Parent { get; set; }
}

public class Tree : OneToMany<DemoArticle, DemoArticle> {
    public class Parent : One { }
    public class Children : Many { }
}
