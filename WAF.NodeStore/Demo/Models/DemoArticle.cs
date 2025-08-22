
using WAF.Nodes;
using WAF.Common;
namespace WAF.Demo.Models;

public class DemoArticle {
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public FileValue File { get; set; } = FileValue.Empty;
}

public class ArticleT {
    public Guid Id { get; set; }
    public Marriage.Node Spouse { get; set; } = Marriage.Empty;
    public Tree.FromNode Parent { get; set; } = Tree.EmptyFrom;
    public Tree.ToNodes Children { get; set; } = Tree.EmptyTo;
    public Related.FromNodes RelatingToMe { get; set; } = Related.EmptyFrom;
    public Related.ToNodes MeRelatingTo { get; set; } = Related.EmptyTo;
}

public class ArticleParent { }

public class ArticleChild { }

public class Person { }

public static class Relations { public static Tree Tree = new(); }

public class Tree : OneToMany<ArticleParent, ArticleChild, Tree> { }

public class Marriage : OneOne<Person, Marriage> { }

public class Related : ManyToMany<ArticleT, ArticleT, Related> { }


