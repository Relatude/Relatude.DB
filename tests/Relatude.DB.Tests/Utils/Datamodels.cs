using Relatude.DB.Common;
using Relatude.DB.Nodes;

namespace Relatude.DB.Utils;

[Node(TextIndex = BoolValue.True)]
public class Article {
    [InternalIdProperty]
    public int Id { get; set; }
    [PublicIdProperty]
    public Guid PId { get; set; }
    [IntegerProperty(Indexed = true, ExcludeFromTextIndex = true)]
    public int Id2 { get; set; }
    [StringProperty(Indexed = true, ExcludeFromTextIndex = true)]
    public string Name { get; set; } = string.Empty;
    //[StringProperty( IndexedByWords = true)]
    public string Body { get; set; } = string.Empty;
    [IntegerProperty(Indexed = true)]
    public int IntegerNum { get; set; }
    public double DoubleNum { get; set; }
    public User? Author { get; set; }
    public Article? Parent { get; set; }
    public IEnumerable<Article> Children { get; set; } = [];
    public override string ToString() => Name;
    public Sizes Size { get; set; }
    public FileValue File { get; set; } = FileValue.Empty;
}
public class Article2 : Article {
    [StringProperty(Indexed = true, IndexedByWords = true, DefaultValue = "dddd")]
    public string? Name2 { get; set; }
}
public class User {
    public Guid Id { get; set; }
    public string? Username { get; set; }
    public IEnumerable<Article>? Articles { get; set; }
    public Group? Group { get; set; }
    public override string ToString() => Username + "";
}
public class Group {
    public Guid Id { get; set; }
    public string? Groupname { get; set; }
    public IEnumerable<User>? Members { get; set; }
    public override string ToString() => Groupname + "";
}

public enum Sizes {
    Small,
    Medium,
    Large
}