
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;
namespace Relatude.DB.Demo.Models;

public interface IDemoArticle {

    public Guid Id { get; set; }

    public string Title { get; set; }
    public string Content { get; set; }
    public int Size { get; set; }

    public NodeMeta Meta { get; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public FileValue File { get; set; }

    public Tree.Children Children { get; set; }
    public Embedded<DemoParagraph> Paragraphs { get; set; }

}
public interface IDemoParagraph {
    public Guid Id { get; set; }
    public FileValue File { get; set; }
    public string Code { get; set; }
    //[EmbeddedMapProperty(KeyProperty = nameof(DemoParagraph.Code))]
    //public Embedded<string, DemoParagraph> SubParagraphs { get; set; } = [];
}


public class DemoArticle{

    public Guid Id { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Size { get; set; }

    public NodeMeta Meta { get; set; } = NodeMeta.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public FileValue File { get; set; } = FileValue.Empty;

    [EmbeddedMapProperty(KeyProperty = nameof(DemoParagraph.Code))]
    public EmbeddedMap<string, DemoParagraph> Paragraphs { get; set; } = [];

    public Tree.Parent Parent { get; set; } = new();
    public Tree.Children Children { get; set; } = new();

    [AddressProperty]
    public string Address { get; set; } = string.Empty;

    [DisplayNameProperty]
    public string DisplayName { get; set; } = string.Empty;

}

public class DemoParagraph {
    public Guid Id { get; set; }
    public FileValue File { get; set; } = FileValue.Empty;
    public string Code { get; set; } = string.Empty;

    //[EmbeddedMapProperty(KeyProperty = nameof(DemoParagraph.Code))]
    //public Embedded<string, DemoParagraph> SubParagraphs { get; set; } = [];

}

public class Tree : OneToMany<DemoArticle, DemoArticle> {
    public class Parent : One { }
    public class Children : Many { }
}
