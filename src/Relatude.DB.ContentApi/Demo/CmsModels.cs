using Relatude.DB.Nodes;

namespace Relatude.DB.ContentApi.Demo;

// A small CMS-like demo model. The content API and UI are fully generic and
// work against any datamodel registered with the store - these classes only
// provide something meaningful to edit out of the box.

public class Article {
    public Guid Id { get; set; }

    [StringProperty(DisplayName = true)]
    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;
    public string Summary2 { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime PublishedUtc { get; set; }
    public bool IsPublished { get; set; }
    public int ViewCount { get; set; }

    public Author? Author { get; set; }
    public Category? Category { get; set; }
    public ArticleTags.Tags Tags { get; set; } = new();
}

// Plain navigation property pairs are inferred as one-to-many relations,
// so a many-to-many relation must be declared explicitly like this.
public class ArticleTags : ManyToMany<Article, Tag> {
    public class Tags : ManyTo { }
    public class Articles : ManyFrom { }
}

public class Author {
    public Guid Id { get; set; }

    [StringProperty(DisplayName = true)]
    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;
    public string Bio { get; set; } = string.Empty;

    public Article[] Articles { get; set; } = [];
}

public class Category {
    public Guid Id { get; set; }

    [StringProperty(DisplayName = true)]
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public Article[] Articles { get; set; } = [];
}

public class Tag {
    public Guid Id { get; set; }

    [StringProperty(DisplayName = true)]
    public string Name { get; set; } = string.Empty;

    public ArticleTags.Articles Articles { get; set; } = new();
}
