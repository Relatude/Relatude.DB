// A CSharpCodeFile datamodel source: this file is NOT part of the compiled project (it is excluded
// in Website.Simple.csproj). relatude.db.json points at the Models/CSharp folder, and every .cs
// file in it is compiled and loaded when the store opens, so the model can be changed without
// rebuilding the website. The types only exist at runtime: application code reaches them by name
// (untyped queries, GraphQL or the admin UI) - see the /campaigns endpoint in Program.cs.
using Relatude.DB.Common;
using Relatude.DB.Nodes;

namespace Website.Simple.RuntimeModels;

[Node(TextIndex = BoolValue.True)]
public class Campaign {
    [PublicIdProperty]
    public Guid Id { get; set; }
    [StringProperty(Indexed = true)]
    public string Name { get; set; } = "";
    public string Pitch { get; set; } = ""; // part of the free text index, used by WhereSearch
    [DoubleProperty(Indexed = true)]
    public double DiscountPercent { get; set; }
    [DateTimeProperty(Indexed = true)]
    public DateTime ValidTo { get; set; }
}
