
using Relatude.DB.Common;
using Relatude.DB.Nodes;
using System.Text.Json.Serialization;
namespace Relatude.DB.Demo.Models;

public class DemoArticle {
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public FileValue File { get; set; } = FileValue.Empty;
    public Tree.FromNode Parent { get; set; } = Tree.EmptyFrom;
    public Tree.ToNodes Children { get; set; } = Tree.EmptyTo;
    
    [RelationProperty<OneDemoArticleManyDemoArticleChildren>(RightToLeft = false)]
    public DemoArticleChild? Child { get; set; }
}

public sealed class OneDemoArticleManyDemoArticleChildren : OneToOne<DemoArticle, DemoArticleChild>;

public sealed class DemoArticleChild
{
    public Guid Id { get; set; }
    public DateTimeOffset DateOfBirth { get; set; }
    
    [RelationProperty<OneDemoArticleManyDemoArticleChildren>(RightToLeft = true)]
    public DemoArticle? Parent { get; set; }
}

public class Tree : OneToMany<DemoArticle, DemoArticle, Tree> { }
