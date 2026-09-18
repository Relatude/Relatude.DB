using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relatude.DB.Demo.Wikipedia;

// The shape of one line of the corpus, as the Wikipedia Corpus Builder writes it. Null fields are
// omitted rather than written as null, which is why everything optional is nullable here.

public sealed class WikiCorpusArticle {
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("wikidataId")] public string? WikidataId { get; set; }
    [JsonPropertyName("lang")] public string Lang { get; set; } = "en";
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("revised")] public DateTimeOffset? Revised { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
    [JsonPropertyName("sections")] public List<WikiCorpusSection>? Sections { get; set; }
    [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
    [JsonPropertyName("topics")] public List<WikiCorpusTopic>? Topics { get; set; }
    [JsonPropertyName("links")] public List<string>? Links { get; set; }
    [JsonPropertyName("images")] public List<WikiCorpusImage>? Images { get; set; }
    [JsonPropertyName("coordinates")] public WikiCorpusPoint? Coordinates { get; set; }
    [JsonPropertyName("popularity")] public double? Popularity { get; set; }
    [JsonPropertyName("textLength")] public int TextLength { get; set; }
}

public sealed class WikiCorpusSection {
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
}

public sealed class WikiCorpusTopic {
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("score")] public double Score { get; set; }
}

public sealed class WikiCorpusImage {
    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("descriptionUrl")] public string? DescriptionUrl { get; set; }
    [JsonPropertyName("caption")] public string? Caption { get; set; }
    [JsonPropertyName("alt")] public string? Alt { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("placement")] public string? Placement { get; set; }
    [JsonPropertyName("isLead")] public bool IsLead { get; set; }
}

public sealed class WikiCorpusPoint {
    [JsonPropertyName("lat")] public double Lat { get; set; }
    [JsonPropertyName("lon")] public double Lon { get; set; }
}

/// <summary>
/// Streams a JSONL corpus one article at a time. The corpus is tens of gigabytes - the English
/// Wikipedia build is 66 GB - so nothing is ever held beyond the line being parsed, and progress is
/// reported as bytes consumed rather than a count, because the number of lines is not known without
/// reading them all.
///
/// Handles a plain <c>.jsonl</c> and a gzipped <c>.jsonl.gz</c>; only the plain form can report a
/// meaningful position, since a compressed stream's position is its compressed one.
/// </summary>
public sealed class WikiCorpusReader : IDisposable {
    readonly FileStream _file;
    readonly StreamReader _reader;

    public WikiCorpusReader(string path) {
        if (!File.Exists(path)) throw new FileNotFoundException("Corpus file not found. ", path);
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        Length = _file.Length;
        var gzipped = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        Stream stream = gzipped ? new GZipStream(_file, CompressionMode.Decompress) : _file;
        _reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, 1 << 20);
    }

    /// <summary>Size of the file on disk, which is what <see cref="Position"/> is measured against.</summary>
    public long Length { get; }

    /// <summary>How far into the file the reader has got. Buffered, so it moves in jumps.</summary>
    public long Position => _file.Position;

    /// <summary>Lines that were not valid JSON. A corpus truncated by a cancelled build ends in one.</summary>
    public long MalformedLines { get; private set; }

    /// <summary>Reads the next article, or returns null at the end of the corpus.</summary>
    public WikiCorpusArticle? Read() {
        while (true) {
            var line = _reader.ReadLine();
            if (line == null) return null;
            if (line.Length == 0) continue;
            try {
                var article = JsonSerializer.Deserialize<WikiCorpusArticle>(line, options);
                if (article != null && article.Title.Length > 0) return article;
            } catch (JsonException) {
                MalformedLines++;
            }
        }
    }

    static readonly JsonSerializerOptions options = new() {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public void Dispose() {
        _reader.Dispose();
        _file.Dispose();
    }
}
