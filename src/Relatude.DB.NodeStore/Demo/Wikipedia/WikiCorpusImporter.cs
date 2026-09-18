using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Relatude.DB.Common;
using Relatude.DB.Demo.Models;
using Relatude.DB.Nodes;

namespace Relatude.DB.Demo.Wikipedia;

/// <summary>What to import, and from where.</summary>
public sealed class WikiImportOptions {
    /// <summary>The JSONL corpus, plain or gzipped. Required.</summary>
    public string CorpusPath { get; set; } = string.Empty;

    /// <summary>A .wikimg image bundle to take picture bytes from, written by the Wikipedia Corpus
    /// Builder. Empty imports the image references only, with no bytes.</summary>
    public string ImageBundlePath { get; set; } = string.Empty;

    /// <summary>How many articles to add. The run stops when it has created this many.</summary>
    public int MaxArticles { get; set; } = 1000;

    /// <summary>Import the picture bytes, not just the references. Needs <see cref="ImageBundlePath"/>.</summary>
    public bool ImportImageFiles { get; set; } = true;

    /// <summary>Take only each article's lead picture. Off takes every picture the bundle carries
    /// for it, which is roughly twice the files.</summary>
    public bool LeadImageOnly { get; set; } = true;

    /// <summary>Ceiling on pictures per article when <see cref="LeadImageOnly"/> is off.</summary>
    public int MaxImagesPerArticle { get; set; } = 8;

    /// <summary>Refuse a picture larger than this rather than pulling a 30 MB original into the store.</summary>
    public long MaxBytesPerImage { get; set; } = 8L * 1024 * 1024;

    /// <summary>Keep the section structure. Off drops it and keeps the joined body text only.</summary>
    public bool ImportSections { get; set; } = true;

    /// <summary>Build the article link graph. Only does anything for a corpus built with --links.</summary>
    public bool ImportLinks { get; set; } = true;

    /// <summary>Articles per transaction. Large enough that the per transaction cost disappears,
    /// small enough that cancelling is quick and progress moves.</summary>
    public int ChunkSize { get; set; } = 200;
}

/// <summary>What one import run did.</summary>
public sealed class WikiImportResult {
    public int Articles { get; set; }
    public int ArticlesSkipped { get; set; }
    public int Categories { get; set; }
    public int Topics { get; set; }
    public int Images { get; set; }
    public int ImageFiles { get; set; }
    public long ImageBytes { get; set; }
    public int Sections { get; set; }
    public int Links { get; set; }
    public long CorpusLinesRead { get; set; }
    public long CorpusBytesRead { get; set; }
    public double ElapsedMs { get; set; }
    /// <summary>Set when a bundle was configured but had nothing for an article. A few of these are
    /// normal - a bundle built with a limit does not cover the whole corpus - and a lot of them means
    /// the bundle and the corpus do not belong together.</summary>
    public long ArticlesMissingFromBundle { get; set; }
    public long ImagesMissingFromBundle { get; set; }
}

/// <summary>
/// Loads a Wikipedia corpus into the demo datamodel.
///
/// Both inputs come from the Wikipedia Corpus Builder and are deliberately dull to read: a JSONL
/// file of one article per line, and a <c>.wikimg</c> bundle of the pictures with an index. The
/// awkward parts - decompressing a Kiwix archive, matching article HTML to Commons file names -
/// happen there, once, so this side needs nothing but <c>System.Text.Json</c> and a file handle.
///
/// Neither file fits in memory - the English corpus is 62 GB - so nothing is read whole: the corpus
/// streams line by line and the bundle is looked up per article. Work is committed one chunk of
/// articles at a time, which is what makes cancelling quick and keeps what was already imported.
///
/// The whole thing is idempotent. Every node's id is derived from its natural key - the page id for
/// an article, the folded name for a category, the path for a topic, the file name for a picture -
/// so running it twice adds the articles that were missing and relates the rest without duplicating
/// anything. That is also what makes categories, topics and pictures shared across articles rather
/// than copied into each one.
/// </summary>
public sealed class WikiCorpusImporter : IDisposable {
    readonly NodeStore _store;
    readonly WikiImportOptions _options;
    readonly WikiImageBundle? _imageSource;

    // Ids seen during this run, so the second article in a category does not re-check the store.
    readonly HashSet<Guid> _categories = [];
    readonly HashSet<Guid> _topics = [];
    readonly HashSet<Guid> _seenImages = [];

    readonly Guid _pCategories, _pTopics, _pImages, _pTopicParent, _pLinksTo;

    public WikiCorpusImporter(NodeStore store, WikiImportOptions options) {
        _store = store;
        _options = options;
        if (options.ImportImageFiles && !string.IsNullOrWhiteSpace(options.ImageBundlePath)) {
            _imageSource = WikiImageBundle.Open(options.ImageBundlePath);
        }
        // Relation property ids, resolved once: the expression overloads of SetRelation would
        // recompile the lambda on every single call, and there are several per article.
        _pCategories = store.Mapper.GetProperty<IWikiArticle>(a => a.Categories).Id;
        _pTopics = store.Mapper.GetProperty<IWikiArticle>(a => a.Topics).Id;
        _pImages = store.Mapper.GetProperty<IWikiArticle>(a => a.Images).Id;
        _pTopicParent = store.Mapper.GetProperty<IWikiTopic>(t => t.Parent).Id;
        _pLinksTo = store.Mapper.GetProperty<IWikiArticle>(a => a.LinksTo).Id;
    }

    /// <summary>True when this datamodel has the Wikipedia demo types at all.</summary>
    public static bool IsSupportedBy(NodeStore store) {
        var fullName = typeof(IWikiArticle).FullName;
        return store.Datamodel.NodeTypes.Values.Any(t => t.FullName == fullName);
    }

    /// <summary>
    /// Runs the import. <paramref name="progress"/> is called with a line for the user and a
    /// percentage; cancelling stops at the next chunk boundary and keeps everything committed so far.
    /// </summary>
    public async Task<WikiImportResult> RunAsync(Action<string, int>? progress, CancellationToken cancellation) {
        var watch = Stopwatch.StartNew();
        var result = new WikiImportResult();
        var target = Math.Max(1, _options.MaxArticles);

        using var reader = new WikiCorpusReader(_options.CorpusPath);
        var batch = new List<WikiCorpusArticle>(_options.ChunkSize);

        while (result.Articles < target) {
            cancellation.ThrowIfCancellationRequested();

            // ---- collect a chunk of articles that are not already stored ----
            batch.Clear();
            var endOfCorpus = false;
            while (batch.Count < _options.ChunkSize && result.Articles + batch.Count < target) {
                var article = reader.Read();
                if (article == null) { endOfCorpus = true; break; }
                result.CorpusLinesRead++;
                if (_store.Exists<IWikiArticle>(ArticleId(article.Id))) {
                    result.ArticlesSkipped++;
                    // A resumed import walks past what it already has. On a big corpus that is a
                    // long silent stretch, so it reports rather than looking hung.
                    if (result.ArticlesSkipped % 5000 == 0)
                        progress?.Invoke("Skipping " + result.ArticlesSkipped.ToString("N0") + " articles already stored…", percent(result.Articles, target));
                    continue;
                }
                batch.Add(article);
            }
            if (batch.Count == 0) break;

            // ---- pull the image bytes for this chunk out of the bundle ----
            // Done before the transaction because the byte count and the real content type belong on
            // the image node, and done per chunk so only a chunk's worth of pictures is ever held.
            var bytesByArticle = new Dictionary<long, Dictionary<string, WikiImageBytes>>();
            if (_imageSource != null) {
                foreach (var article in batch) {
                    cancellation.ThrowIfCancellationRequested();
                    var wanted = wantedImages(article);
                    if (wanted.Count == 0) continue;
                    bytesByArticle[article.Id] = _imageSource.ReadImages(article.Id, wanted, _options.MaxBytesPerImage);
                }
            }

            // ---- one transaction for the chunk ----
            var t = _store.CreateTransaction();
            t.BulkInsert = true; // written nodes leave the cache, so a long run stays flat in memory
            var uploads = new List<(Guid NodeId, string FileName, WikiImageBytes Bytes)>();

            foreach (var article in batch) {
                cancellation.ThrowIfCancellationRequested();
                bytesByArticle.TryGetValue(article.Id, out var bytes);
                addArticle(t, article, bytes, uploads, result);
            }
            await _store.ExecuteAsync(t);

            // ---- the file bytes, once the image nodes exist ----
            // A FileValue carries the path of the property it belongs to, which a node that has not
            // been stored yet does not have, so this cannot be folded into the transaction above.
            foreach (var (nodeId, fileName, image) in uploads) {
                cancellation.ThrowIfCancellationRequested();
                try {
                    using var stream = new MemoryStream(image.Data, writable: false);
                    await _store.FileUploadAsync<IWikiImage>(nodeId, i => i.File, stream, fileName);
                    result.ImageFiles++;
                    result.ImageBytes += image.Data.Length;
                } catch (Exception) {
                    // A file store that refuses one picture should not lose the whole chunk of
                    // articles that were already committed above.
                }
            }

            result.CorpusBytesRead = reader.Position;
            progress?.Invoke(describe(result, reader), percent(result.Articles, target));
            if (endOfCorpus) break;
        }

        if (_imageSource != null) {
            result.ArticlesMissingFromBundle = _imageSource.ArticlesNotFound;
            result.ImagesMissingFromBundle = _imageSource.ImagesNotFound;
        }
        result.ElapsedMs = watch.Elapsed.TotalMilliseconds;
        return result;
    }

    static int percent(int done, int target) => (int)Math.Clamp(100L * done / Math.Max(1, target), 0, 100);

    static string describe(WikiImportResult r, WikiCorpusReader reader) {
        var sb = new StringBuilder();
        sb.Append(r.Articles.ToString("N0")).Append(" articles");
        if (r.Categories > 0) sb.Append(", ").Append(r.Categories.ToString("N0")).Append(" categories");
        if (r.Images > 0) sb.Append(", ").Append(r.Images.ToString("N0")).Append(" images");
        if (r.ImageFiles > 0) sb.Append(" (").Append(r.ImageFiles.ToString("N0")).Append(" files)");
        if (reader.Length > 0) sb.Append(" · ").Append((100.0 * reader.Position / reader.Length).ToString("0.0")).Append("% of the corpus read");
        return sb.ToString();
    }

    List<string> wantedImages(WikiCorpusArticle article) {
        if (article.Images == null || article.Images.Count == 0) return [];
        var names = new List<string>();
        foreach (var image in article.Images) {
            if (image.File.Length == 0) continue;
            if (_options.LeadImageOnly && !image.IsLead) continue;
            names.Add(image.File);
            if (_options.LeadImageOnly) break;
            if (names.Count >= _options.MaxImagesPerArticle) break;
        }
        // An article whose corpus entry marks no lead image still has pictures; take the first.
        if (names.Count == 0 && _options.LeadImageOnly) {
            var first = article.Images.FirstOrDefault(i => i.File.Length > 0);
            if (first != null) names.Add(first.File);
        }
        return names;
    }

    void addArticle(Transaction t, WikiCorpusArticle source, Dictionary<string, WikiImageBytes>? bytes,
                    List<(Guid, string, WikiImageBytes)> uploads, WikiImportResult result) {
        var id = ArticleId(source.Id);
        var article = _store.Mapper.NewObjectFromType<IWikiArticle>(new NodeKey(id));
        article.Title = trim(source.Title, 512);
        article.PageId = source.Id;
        article.WikidataId = trim(source.WikidataId, 32);
        article.Language = trim(source.Lang, 16);
        article.Url = trim(source.Url, 1000);
        article.Address = addressOf(source);
        article.RevisedUtc = source.Revised?.UtcDateTime ?? DateTime.MinValue;
        article.Summary = trim(source.Summary, 8000);
        article.Text = source.Text;
        article.TextLength = source.TextLength > 0 ? source.TextLength : source.Text.Length;
        article.Popularity = source.Popularity ?? 0;
        article.Location = source.Coordinates == null
            ? GeoCoordinate.Empty
            : new GeoCoordinate(source.Coordinates.Lat, source.Coordinates.Lon);
        article.ImageCount = source.Images?.Count ?? 0;

        if (_options.ImportSections && source.Sections != null) {
            var anchors = new HashSet<string>(StringComparer.Ordinal);
            var ordinal = 0;
            foreach (var section in source.Sections) {
                article.Sections.Add(new WikiSection {
                    Id = Guid.NewGuid(),
                    Anchor = anchorFor(section.Title, ordinal, anchors),
                    Heading = trim(section.Title, 512),
                    Level = section.Level,
                    Ordinal = ordinal,
                    Text = section.Text,
                });
                ordinal++;
                result.Sections++;
            }
        }

        // The topic scores go on the article; the topics themselves are nodes, below.
        if (source.Topics != null) {
            foreach (var topic in source.Topics) {
                if (topic.Name.Length == 0) continue;
                article.TopicScores.Add(new WikiTopicScore { Id = Guid.NewGuid(), TopicName = trim(topic.Name, 200), Score = topic.Score });
            }
            var best = source.Topics.OrderByDescending(x => x.Score).FirstOrDefault();
            if (best != null) {
                article.PrimaryTopic = trim(best.Name, 200);
                article.PrimaryTopicScore = best.Score;
            }
        }

        // The pictures go in first. A reference is validated against a node that must already
        // exist, and the article carries one to its lead picture, so the image nodes have to be
        // earlier in this transaction than the article that points at them.
        var imageIds = new List<Guid>();
        if (source.Images != null) {
            foreach (var image in source.Images) {
                if (image.File.Length == 0) continue;
                var imageId = addImage(t, image, bytes, uploads, result);
                if (imageId == Guid.Empty) continue;
                imageIds.Add(imageId);
                // a handful of articles list hundreds of pictures; the relation list is not the point
                if (imageIds.Count >= 64) break;
            }
        }

        // The lead picture, as a reference. Set before the insert because a reference is node data.
        var leadName = source.Images?.FirstOrDefault(i => i.IsLead && i.File.Length > 0)?.File
                       ?? source.Images?.FirstOrDefault(i => i.File.Length > 0)?.File;
        if (leadName != null) {
            var leadId = ImageId(leadName);
            if (imageIds.Contains(leadId)) article.LeadImage.Set(leadId);
        }

        t.InsertIfNotExists(article, ignoreRelated: true);
        // The system display name and address, which the admin UI and routing read off NodeMeta.
        // Set here rather than through [DisplayNameProperty] / [AddressProperty]: a marker attribute
        // takes the member's property model away with it, and Title and Address are wanted as real
        // indexed properties. Ordering matters - the node must exist first, which it does because
        // the insert above is earlier in the same transaction.
        t.UpdateIfDifferentDisplayName(id, article.Title);
        t.UpdateIfDifferentAddress(id, article.Address);
        result.Articles++;

        if (source.Categories != null) {
            foreach (var name in source.Categories) {
                var categoryId = addCategory(t, name, result);
                if (categoryId != Guid.Empty) t.SetRelation(id, _pCategories, categoryId);
            }
        }
        if (source.Topics != null) {
            foreach (var topic in source.Topics) {
                var topicId = addTopic(t, topic.Name, result);
                if (topicId != Guid.Empty) t.SetRelation(id, _pTopics, topicId);
            }
        }
        foreach (var imageId in imageIds) t.SetRelation(id, _pImages, imageId);
        if (_options.ImportLinks && source.Links != null) {
            // The link graph is the one part of this that cannot be computed: an article's id comes
            // from its page id and a link gives only a title, so each one is an indexed lookup on
            // the address. Only links whose target is already stored can be related - a second run
            // over the same corpus fills in what the first could not yet see. Corpora built without
            // --links carry none of this, which is the usual case and costs nothing.
            var linked = 0;
            foreach (var linkTitle in source.Links) {
                if (linkTitle.Length == 0) continue;
                var address = "/wiki/" + linkTitle.Replace(' ', '_');
                var toId = _store.Query<IWikiArticle>().Where(a => a.Address == address).Page(0, 1).SelectId().Execute().FirstOrDefault();
                if (toId == Guid.Empty || toId == id) continue;
                t.SetRelation(id, _pLinksTo, toId);
                result.Links++;
                if (++linked >= 200) break;
            }
        }
    }

    Guid addCategory(Transaction t, string name, WikiImportResult result) {
        var key = name.Trim();
        if (key.Length == 0) return Guid.Empty;
        var id = CategoryId(key);
        if (!_categories.Add(id)) return id;
        var category = _store.Mapper.NewObjectFromType<IWikiCategory>(new NodeKey(id));
        category.Title = trim(key, 512);
        category.Key = trim(key.ToLowerInvariant(), 512);
        t.InsertIfNotExists(category, ignoreRelated: true);
        t.UpdateIfDifferentDisplayName(id, category.Title);
        result.Categories++;
        return id;
    }

    /// <summary>
    /// Adds a topic and every ancestor of its dotted path, linking each to its parent, so the
    /// taxonomy comes out as a tree rather than a flat list of strings.
    /// </summary>
    Guid addTopic(Transaction t, string path, WikiImportResult result) {
        var clean = path.Trim();
        if (clean.Length == 0) return Guid.Empty;
        var segments = clean.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return Guid.Empty;

        var leafId = Guid.Empty;
        var parentId = Guid.Empty;
        for (var i = 0; i < segments.Length; i++) {
            var subPath = string.Join('.', segments[..(i + 1)]);
            var id = TopicId(subPath);
            if (_topics.Add(id)) {
                var topic = _store.Mapper.NewObjectFromType<IWikiTopic>(new NodeKey(id));
                topic.Title = trim(segments[i], 512);
                topic.Path = trim(subPath, 200);
                topic.Depth = i + 1;
                t.InsertIfNotExists(topic, ignoreRelated: true);
                t.UpdateIfDifferentDisplayName(id, topic.Title);
                result.Topics++;
                if (parentId != Guid.Empty) t.SetRelation(id, _pTopicParent, parentId);
            }
            parentId = id;
            leafId = id;
        }
        return leafId;
    }

    Guid addImage(Transaction t, WikiCorpusImage source, Dictionary<string, WikiImageBytes>? bytes,
                  List<(Guid, string, WikiImageBytes)> uploads, WikiImportResult result) {
        var id = ImageId(source.File);
        if (!_seenImages.Add(id)) return id;
        // Pictures are shared, so one that another article already brought in is left alone -
        // including its file, which is the whole reason the bytes are not stored per article.
        if (_store.Exists<IWikiImage>(id)) return id;

        var image = default(WikiImageBytes);
        var hasBytes = bytes != null && bytes.TryGetValue(source.File, out image);

        var node = _store.Mapper.NewObjectFromType<IWikiImage>(new NodeKey(id));
        node.Title = trim(source.File, 512);
        node.FileName = trim(source.File, 512);
        node.Caption = trim(source.Caption, 2000);
        node.Alt = trim(source.Alt, 2000);
        node.DescriptionUrl = trim(source.DescriptionUrl, 1000);
        node.SourceUrl = trim(source.Url, 1000);
        node.Width = source.Width ?? 0;
        node.Bytes = hasBytes ? image.Data.Length : 0;
        t.InsertIfNotExists(node, ignoreRelated: true);
        t.UpdateIfDifferentDisplayName(id, node.Title);
        result.Images++;

        if (hasBytes) uploads.Add((id, storedFileName(source.File, image.FileExtension), image));
        return id;
    }

    /// <summary>What the file is called in the store. The archive serves WebP for files still named
    /// ".jpg", so the extension follows the bytes rather than the name they arrived under.</summary>
    static string storedFileName(string wikiFileName, string extension) {
        var name = Path.GetFileNameWithoutExtension(wikiFileName);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length > 120) name = name[..120];
        if (name.Length == 0) name = "image";
        return name + extension;
    }

    static string addressOf(WikiCorpusArticle article) {
        // The wiki path, which is stable and unique per article. Taken from the canonical URL when
        // there is one, because that is already escaped the way Wikipedia escapes it.
        if (!string.IsNullOrEmpty(article.Url)) {
            var at = article.Url.IndexOf("/wiki/", StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return trim(article.Url[at..], 1000);
        }
        return trim("/wiki/" + article.Title.Replace(' ', '_'), 1000);
    }

    /// <summary>A section key that is unique within its article: the heading, slugified, with the
    /// position appended if two headings happen to slug the same.</summary>
    static string anchorFor(string heading, int ordinal, HashSet<string> taken) {
        var sb = new StringBuilder(heading.Length);
        foreach (var c in heading) {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0) slug = ordinal == 0 ? "lead" : "section";
        if (slug.Length > 80) slug = slug[..80];
        if (taken.Add(slug)) return slug;
        var unique = slug + "-" + ordinal;
        taken.Add(unique);
        return unique;
    }

    static string trim(string? value, int max) {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= max ? value : value[..max];
    }

    // -----------------------------------------------------------------------------------------
    // Deterministic ids
    // -----------------------------------------------------------------------------------------
    // Every node's id is a hash of its natural key, which is what makes the import idempotent and
    // what lets two articles that use the same category or picture agree on its id without either
    // of them looking it up. The name spaces keep an article and a category with the same text from
    // colliding. This is RFC 4122's name based scheme with SHA-256 in place of SHA-1.

    public static Guid ArticleId(long pageId) => idFor("wiki:article", pageId.ToString());
    public static Guid CategoryId(string name) => idFor("wiki:category", name.Trim().ToLowerInvariant());
    public static Guid TopicId(string path) => idFor("wiki:topic", path.Trim().ToLowerInvariant());
    public static Guid ImageId(string fileName) => idFor("wiki:image", fileName.Trim().Replace('_', ' ').ToLowerInvariant());

    static Guid idFor(string nameSpace, string key) {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(nameSpace + " " + key));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80); // version 8: a name based id from a custom hash
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }

    public void Dispose() => _imageSource?.Dispose();
}
