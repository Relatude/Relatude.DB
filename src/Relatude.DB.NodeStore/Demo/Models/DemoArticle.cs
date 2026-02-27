
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;
namespace Relatude.DB.Demo.Models;


public class DemoArticle {

    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Size { get; set; }

    public NodeMeta Meta { get; set; } = NodeMeta.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public FileValue File { get; set; } = FileValue.Empty;

    public Tree.Parent Parent { get; set; } = new();
    public Tree.Children Children { get; set; } = new();

}


public class Tree : OneToMany<DemoArticle, DemoArticle> {
    public class Parent : One { }
    public class Children : Many { }
}
