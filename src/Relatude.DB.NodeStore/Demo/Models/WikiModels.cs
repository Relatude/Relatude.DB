using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Nodes;

namespace Relatude.DB.Demo.Models;

/// <summary>
/// The demo datamodel for a Wikipedia corpus - the JSONL produced by the Wikipedia Corpus Builder,
/// one complete article per line, with its structure intact.
///
/// The old <see cref="DemoArticle"/> kept a title and a blob of text, which is enough to try search
/// on and nothing else. A real corpus carries far more than that: sections, categories, predicted
/// topics, images, coordinates and a popularity score. Every one of those maps onto something the
/// engine can actually do, so the model below spends each of them on the feature it fits:
///
///   sections, topic confidences  -> embedded data, owned by the article and meaningless without it
///   categories, topics, images   -> node types joined by relations, shared and traversable
///   links between articles       -> a self-referential relation, so Traverse and ShortestPath work
///   the one representative image -> a Reference, a cheap pointer with no reverse index
///   image bytes                  -> a FileValue, so the file store and the image converters are in play
///   coordinates                  -> a GeoCoordinate, so IsWithin is accelerated by the spatial index
///   popularity, text length      -> indexed numbers, so ranges, sorting and range facets work
///
/// Everything here lives in the same namespace as the rest of the demo model, which is the namespace
/// the default relatude.db.json already points at, so a default installation picks these types up
/// with no configuration change.
/// </summary>
///
/// <remarks>
/// Every type and relation pins its id. Without one the id is a hash of the full type name, so a
/// rename or a namespace move would silently create a new type with no data.
/// </remarks>

// ---------------------------------------------------------------------------------------------
// The facet interface
// ---------------------------------------------------------------------------------------------

/// <summary>
/// What every node in the corpus has: a name. Declaring it once, on an interface the four concrete
/// types implement, makes it a parent node type of all of them - so one query can search articles,
/// categories, topics and images together instead of four queries merged in memory:
/// <code>db.Query&lt;IWikiNode&gt;().WhereSearch("turing").Execute()</code>
/// </summary>
[Node(Id = "3085aadc-e5a1-4a81-b4bc-3216d5184c9c")]
public interface IWikiNode {
    Guid Id { get; set; }

    /// <summary>
    /// Article title, category name, topic path or image file name, depending on the type.
    /// Word-indexed and prefix-searchable so the admin UI's search box finds any of them.
    /// Not unique: an article and a category can legitimately share a name, and uniqueness
    /// declared here would span every type that inherits it.
    /// <para>
    /// Deliberately not marked <c>[DisplayNameProperty]</c>. A marker attribute turns the member
    /// into a projection of the system value and it stops having a property model of its own, which
    /// would silently drop the index declared here. The importer sets the system display name
    /// explicitly instead, so this stays an ordinary indexed, searchable, sortable property.
    /// </para>
    /// </summary>
    [StringProperty(Indexed = true, IndexedByWords = true, PrefixSearch = true, MaxLength = 512)]
    string Title { get; set; }

    NodeMeta Meta { get; }
}

// ---------------------------------------------------------------------------------------------
// Articles
// ---------------------------------------------------------------------------------------------

[Node(Id = "5af62544-089d-4a00-b376-a8881f856ad1", TextIndex = BoolValue.True)]
public interface IWikiArticle : IWikiNode {

    /// <summary>MediaWiki page id, stable across renames. The node's Guid is derived from it, so it
    /// is the key an import resumes on; indexed but not unique, because a unique index over millions
    /// of articles costs more than the Guid already guarantees.</summary>
    [LongProperty(Indexed = true)]
    long PageId { get; set; }

    /// <summary>Wikidata entity id, e.g. "Q7251". Empty when the page has none.</summary>
    [StringProperty(Indexed = true, MaxLength = 32)]
    string WikidataId { get; set; }

    /// <summary>Language code of the source wiki. Indexed because it is the natural first facet.</summary>
    [StringProperty(Indexed = true, MaxLength = 16)]
    string Language { get; set; }

    /// <summary>Canonical article URL on Wikipedia. Excluded from the text index: a URL full of
    /// percent escapes is noise in a BM25 index and worse in an embedding.</summary>
    [StringProperty(MaxLength = 1000, ExcludeFromTextIndex = true)]
    string Url { get; set; }

    /// <summary>The wiki path, e.g. "/wiki/Alan_Turing". Unique per article in practice, which is
    /// what makes it the key the link graph resolves against - so it is indexed, and for the same
    /// reason as <see cref="IWikiNode.Title"/> it is not marked <c>[AddressProperty]</c>; the
    /// importer sets the system address separately.</summary>
    [StringProperty(Indexed = true, MaxLength = 1000, ExcludeFromTextIndex = true)]
    string Address { get; set; }

    /// <summary>Timestamp of the revision this text came from.</summary>
    [DateTimeProperty(Indexed = true)]
    DateTime RevisedUtc { get; set; }

    /// <summary>Lead paragraph - a ready-made abstract. Short and self-contained, which makes it the
    /// best single field to embed, so it carries a boost over the body in the text index too.</summary>
    [StringProperty(IndexedByWords = true, TextIndexBoost = 3, MaxLength = 8000)]
    string Summary { get; set; }

    /// <summary>Full plain-text body, sections joined in reading order.</summary>
    [StringProperty(IndexedByWords = true)]
    string Text { get; set; }

    /// <summary>Body length in characters. Indexed, so "long articles only" is an index range scan
    /// rather than a scan of every node.</summary>
    [IntegerProperty(Indexed = true)]
    int TextLength { get; set; }

    /// <summary>Relative pageview-based popularity from the corpus. The values span many orders of
    /// magnitude, so the range facet buckets them by powers of ten rather than evenly.</summary>
    [DoubleProperty(Indexed = true, FacetRangePowerBase = 10, FacetRangeCount = 8)]
    double Popularity { get; set; }

    /// <summary>Where the subject is, when it has a place. <see cref="GeoCoordinate.Empty"/> when it
    /// does not - and empty coordinates are kept out of the spatial index entirely, so an article
    /// about a concept never turns up in a "within 5 km" search.</summary>
    [GeoCoordinateProperty(Indexed = true)]
    GeoCoordinate Location { get; set; }

    /// <summary>The highest scoring predicted topic, denormalised onto the article so it can be
    /// faceted and sorted without walking the relation. The full scored list is in
    /// <see cref="TopicScores"/> and the graph edges are in <see cref="Topics"/>.</summary>
    [StringProperty(Indexed = true, MaxLength = 200, ExcludeFromTextIndex = true)]
    string PrimaryTopic { get; set; }

    /// <summary>Confidence of <see cref="PrimaryTopic"/>, 0..1.</summary>
    [DoubleProperty(Indexed = true)]
    double PrimaryTopicScore { get; set; }

    /// <summary>Number of pictures the article uses, including ones whose bytes were never fetched.</summary>
    [IntegerProperty(Indexed = true)]
    int ImageCount { get; set; }

    // -- embedded: owned by this article, no independent identity, never queried on its own --

    /// <summary>
    /// The heading hierarchy, in reading order, keyed by an anchor unique within the article.
    /// Sections are embedded rather than made into nodes deliberately: an article's "Career"
    /// section means nothing detached from the article, and a corpus of seven million articles
    /// would otherwise become a corpus of thirty million nodes.
    /// </summary>
    [EmbeddedMapProperty(KeyProperty = nameof(WikiSection.Anchor))]
    EmbeddedMap<string, WikiSection> Sections { get; }

    /// <summary>
    /// Confidence per predicted topic. This is edge data - it belongs to the pairing of article and
    /// topic, not to either one - and the engine has no edge properties, so it is embedded on the
    /// side that owns it while <see cref="Topics"/> carries the graph edge itself.
    /// </summary>
    [EmbeddedMapProperty(KeyProperty = nameof(WikiTopicScore.TopicName))]
    EmbeddedMap<string, WikiTopicScore> TopicScores { get; }

    // -- relations: shared, indexed, traversable, ordered --

    /// <summary>Wikipedia categories, housekeeping ones already filtered out by the corpus builder.
    /// The category names feed this article's text index, so searching for a category name finds the
    /// articles in it.</summary>
    [RelationProperty(TextIndexRelatedDisplayName = true, TextIndexRecursiveLevelLimit = 1, Facet = true)]
    WikiArticleCategories.Categories Categories { get; }

    /// <summary>Predicted subject areas, most confident first - the relation list keeps that order.</summary>
    [RelationProperty(Facet = true)]
    WikiArticleTopics.Topics Topics { get; }

    /// <summary>Every picture the article uses, in document order.</summary>
    [RelationProperty(Facet = true)]
    WikiArticleImages.Images Images { get; }

    /// <summary>Articles this one links to. Self-referential, so <c>Traverse</c> and
    /// <c>ShortestPath</c> answer "how far is Oslo from Alan Turing" over the real link graph.</summary>
    WikiArticleLinks.LinksTo LinksTo { get; }

    /// <summary>Articles that link to this one - the reverse side, which the relation index maintains
    /// for free. This is the whole difference between a relation and a reference.</summary>
    WikiArticleLinks.LinkedFrom LinkedFrom { get; }

    // -- reference: a one-way pointer, deliberately without a reverse index --

    /// <summary>
    /// The infobox picture: the one image worth showing on a card or a search hit. It is also in
    /// <see cref="Images"/>; this is a separate, cheaper pointer because nothing ever asks an image
    /// which articles lead with it, and a reference is one Guid on the node rather than an edge in
    /// the relation index. Indexed so "articles that have a lead image" is a query.
    /// </summary>
    [ReferenceProperty(Indexed = true)]
    Reference<IWikiImage> LeadImage { get; }
}

// ---------------------------------------------------------------------------------------------
// Categories, topics, images
// ---------------------------------------------------------------------------------------------

/// <summary>
/// A Wikipedia category. Its own node type rather than a string array on the article, because that
/// is what makes "every article in this category" an index lookup instead of a scan, and what lets
/// two articles be two steps apart in the graph.
/// </summary>
[Node(Id = "ebeb7a08-0467-4171-b859-4acf06385235", TextIndex = BoolValue.True, SemanticIndex = BoolValue.False)]
public interface IWikiCategory : IWikiNode {
    /// <summary>Case-folded category name. The node's Guid is derived from it, which is what keeps
    /// the same category from being created twice across an import.</summary>
    [StringProperty(Indexed = true, MaxLength = 512, ExcludeFromTextIndex = true)]
    string Key { get; set; }

    WikiArticleCategories.Articles Articles { get; }
}

/// <summary>
/// A subject area from Wikimedia's article-topic model. The taxonomy is small - tens of entries, not
/// the two million free-form categories - and its names are dotted paths like
/// "Culture.Biography.Biography*", so the importer also hangs them on a parent/child relation and
/// the result is a real tree to traverse.
/// </summary>
[Node(Id = "7777a575-8489-4dec-8a4a-c3b199290623", TextIndex = BoolValue.True, SemanticIndex = BoolValue.False)]
public interface IWikiTopic : IWikiNode {
    /// <summary>The full dotted path. Unique - the taxonomy is small enough that a unique index costs
    /// nothing, and a duplicate here would mean the importer had a bug.</summary>
    [StringProperty(Indexed = true, MaxLength = 200, UniqueValues = true, ExcludeFromTextIndex = true)]
    string Path { get; set; }

    /// <summary>How deep in the dotted path this topic sits, starting at 1.</summary>
    [IntegerProperty(Indexed = true)]
    int Depth { get; set; }

    WikiTopicTree.Parent Parent { get; }
    WikiTopicTree.Children Children { get; }
    WikiArticleTopics.Articles Articles { get; }
}

/// <summary>
/// A picture. One node per distinct file, shared by every article that uses it - the same photograph
/// is the lead image of several articles often enough that giving each article its own copy would
/// store the same bytes many times over.
/// </summary>
[Node(Id = "cb82315b-4400-4dec-bdf8-23109feee058", TextIndex = BoolValue.True, SemanticIndex = BoolValue.False)]
public interface IWikiImage : IWikiNode {
    /// <summary>File name exactly as MediaWiki stores it, e.g. "Alan Turing Aged 16.jpg". The node's
    /// Guid is derived from it, which is what makes the sharing work.</summary>
    [StringProperty(Indexed = true, MaxLength = 512, ExcludeFromTextIndex = true)]
    string FileName { get; set; }

    /// <summary>Caption as the first article to use this picture wrote it, markup already stripped.
    /// Strictly a per-use value; kept here because a caption is the only prose an image has and a
    /// searchable one is worth more than a pedantically placed one.</summary>
    [StringProperty(IndexedByWords = true, MaxLength = 2000)]
    string Caption { get; set; }

    /// <summary>Alt text, when the article supplied one.</summary>
    [StringProperty(MaxLength = 2000)]
    string Alt { get; set; }

    /// <summary>The file's description page, which is where its licence and author are recorded.
    /// Image licences are not the article's CC BY-SA and some files are not redistributable at all,
    /// so this travels with every picture.</summary>
    [StringProperty(MaxLength = 1000, ExcludeFromTextIndex = true)]
    string DescriptionUrl { get; set; }

    /// <summary>Special:FilePath URL, which resolves to the file whether it lives on Commons or the
    /// local wiki - the fallback when the bytes were never fetched.</summary>
    [StringProperty(MaxLength = 1000, ExcludeFromTextIndex = true)]
    string SourceUrl { get; set; }

    /// <summary>Display width the article asked for, 0 when it asked for none.</summary>
    [IntegerProperty(Indexed = true)]
    int Width { get; set; }

    /// <summary>Size of the stored file in bytes, 0 when only the reference was imported.</summary>
    [LongProperty(Indexed = true)]
    long Bytes { get; set; }

    /// <summary>
    /// The picture itself, in the file store. Empty when the corpus supplied a reference but the
    /// bytes were never downloaded, which is the normal case for a corpus built without images -
    /// so treat this as "may be empty" and fall back to <see cref="SourceUrl"/>.
    /// Once set, <c>db.GetUrl(img.File, new FileAdjustmentImage { Width = 200, ... })</c> gives a
    /// converted variant, and the conversion runs in the background.
    /// </summary>
    [FileProperty]
    FileValue File { get; set; }

    WikiArticleImages.Articles Articles { get; }
}

// ---------------------------------------------------------------------------------------------
// Embedded values
// ---------------------------------------------------------------------------------------------

/// <summary>One section of an article, stored inline in it. The lead section has an empty heading
/// at level 1.</summary>
public class WikiSection {
    public Guid Id { get; set; }

    /// <summary>Key within the article: the heading slugified, with the ordinal appended so two
    /// sections that happen to share a heading stay distinct.</summary>
    public string Anchor { get; set; } = string.Empty;

    public string Heading { get; set; } = string.Empty;

    /// <summary>Heading depth: 1 for the lead, 2 for "==", 3 for "===".</summary>
    public int Level { get; set; }

    /// <summary>Position in reading order, from 0.</summary>
    public int Ordinal { get; set; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>The article-topic model's confidence for one topic, stored inline on the article it
/// belongs to.</summary>
public class WikiTopicScore {
    public Guid Id { get; set; }
    public string TopicName { get; set; } = string.Empty;

    /// <summary>Confidence in the range 0..1.</summary>
    public double Score { get; set; }
}

// ---------------------------------------------------------------------------------------------
// Relations
// ---------------------------------------------------------------------------------------------

/// <summary>Articles to the categories they are in.</summary>
[Relation(Id = "411551cb-4ca7-4ae6-9813-956ff92fad51")]
public class WikiArticleCategories : ManyToMany<IWikiArticle, IWikiCategory> {
    public class Categories : ManyTo { }
    public class Articles : ManyFrom { }
}

/// <summary>Articles to their predicted subject areas.</summary>
[Relation(Id = "2eb2076d-a4fe-41e0-9ede-8c935faff75b")]
public class WikiArticleTopics : ManyToMany<IWikiArticle, IWikiTopic> {
    public class Topics : ManyTo { }
    public class Articles : ManyFrom { }
}

/// <summary>Articles to the pictures they use. Many to many because one picture is shared.</summary>
[Relation(Id = "460fc344-e8ea-48c5-a4f7-7eb1103e03cf")]
public class WikiArticleImages : ManyToMany<IWikiArticle, IWikiImage> {
    public class Images : ManyTo { }
    public class Articles : ManyFrom { }
}

/// <summary>The link graph: article to article, in both directions. Circular references are the
/// whole point here, so they are not disallowed.</summary>
[Relation(Id = "b6c22c56-5b4f-4c44-9fce-ff93438b912e")]
public class WikiArticleLinks : ManyToMany<IWikiArticle, IWikiArticle> {
    public class LinksTo : ManyTo { }
    public class LinkedFrom : ManyFrom { }
}

/// <summary>The topic taxonomy's own tree, rebuilt from the dotted paths.</summary>
[Relation(Id = "58662d6f-3cba-49dc-9fe9-1506f1ada0ea", DisallowCircularReferences = true)]
public class WikiTopicTree : OneToMany<IWikiTopic, IWikiTopic> {
    public class Parent : One { }
    public class Children : Many { }
}
