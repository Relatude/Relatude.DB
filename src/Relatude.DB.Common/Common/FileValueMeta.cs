using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Relatude.DB.Common;

[JsonPolymorphic(TypeDiscriminatorPropertyName = KindPropertyName, UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FallBackToBaseType, IgnoreUnrecognizedTypeDiscriminators = true)]
[JsonDerivedType(typeof(FileValueMeta), "file")]
[JsonDerivedType(typeof(DocumentFileMeta), "document")]
[JsonDerivedType(typeof(ImageFileMeta), "image")]
[JsonDerivedType(typeof(AudioFileMeta), "audio")]
[JsonDerivedType(typeof(VideoFileMeta), "video")]
public class FileValueMeta {
    public const string KindPropertyName = "$kind";

    public string? Title { get; set; }
    public string? Description { get; set; }
    public string[]? Keywords { get; set; }
    public string? Author { get; set; }
    public string? Language { get; set; }
    public FileTextExtraction? TextExtraction { get; set; }
    public JsonElement? Technical { get; set; }

    public virtual IEnumerable<string> GetSearchTexts() {
        if (!string.IsNullOrWhiteSpace(Title)) yield return Title;
        if (!string.IsNullOrWhiteSpace(Description)) yield return Description;
        if (Keywords != null) foreach (var keyword in Keywords) if (!string.IsNullOrWhiteSpace(keyword)) yield return keyword;
        if (!string.IsNullOrWhiteSpace(Author)) yield return Author;
    }
    public string GetSearchText() => string.Join(Environment.NewLine, GetSearchTexts());

    public static FileValueMeta CreateFor(FileType fileType) => fileType switch {
        FileType.Image => new ImageFileMeta(),
        FileType.Document => new DocumentFileMeta(),
        FileType.Audio => new AudioFileMeta(),
        FileType.Video => new VideoFileMeta(),
        _ => new FileValueMeta(),
    };

    public static FileValueMeta Parse(string? json, FileType fileType) {
        if (string.IsNullOrWhiteSpace(json)) return CreateFor(fileType);
        try {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(KindPropertyName, out _)) {
                return JsonSerializer.Deserialize<FileValueMeta>(json, JsonOptions) ?? CreateFor(fileType);
            }
            var legacy = CreateFor(fileType);
            legacy.Technical = document.RootElement.Clone();
            return legacy;
        } catch (JsonException) {
            return CreateFor(fileType);
        }
    }
    public FileValueMeta AsKindFor(FileType fileType) {
        var target = CreateFor(fileType);
        if (target.GetType() == GetType()) return this;
        target.Title = Title;
        target.Description = Description;
        target.Keywords = Keywords;
        target.Author = Author;
        target.Language = Language;
        target.TextExtraction = TextExtraction;
        target.Technical = Technical;
        return target;
    }
    public static bool IsTyped(string? json) => json != null && json.StartsWith("{\"" + KindPropertyName + "\"", StringComparison.Ordinal);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static JsonSerializerOptions JsonOptions { get; } = new() {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

public class DocumentFileMeta : FileValueMeta {
    public int? Pages { get; set; }
}

public class ImageFileMeta : FileValueMeta {
    public ImageAiAnalysis? AiAnalysis { get; set; }
    /// <summary>
    /// The point of the picture to keep, in its pixels from the top left: where a crop is placed when the adjustment asking
    /// for a picture of this file names no focus (<c>FileAdjustmentImage.FocusX</c>, <c>FocusY</c>): a Fill, or an Auto that
    /// fills, that cuts the picture, and the window of a zoom. Set by image analysis or an editor. None crops about the middle.
    /// </summary>
    public ImageFocusPoint? FocusPoint { get; set; }
    /// <summary>
    /// How a picture of this file is made to a size of another shape when the adjustment asking for it names no
    /// crop mode (<c>FileAdjustmentImage.CropMode</c>): the picture's own default, which an adjustment's mode
    /// overrides. None leaves the engine's default, Fit.
    /// </summary>
    public FileConversion.ImageCropMode? CropMode { get; set; }
    /// <summary>
    /// What shows behind a picture of this file, where it is see-through and where the canvas asked for is larger
    /// than the picture, when the adjustment asking for it names no background (<c>FileAdjustmentImage.BackgroundColor</c>):
    /// "#RRGGBB" or "#RRGGBBAA". None, or one wholly see-through, leaves the background see-through where the
    /// format asked for can be.
    /// </summary>
    public string? BackgroundColor { get; set; }
    public ImageObjectInfo[]? Objects { get; set; }
    public override IEnumerable<string> GetSearchTexts() {
        foreach (var text in base.GetSearchTexts()) yield return text;
        if (Objects == null) yield break;
        foreach (var name in Objects.Select(o => o.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase)) yield return name!;
    }
}

public abstract class MediaFileMeta : FileValueMeta {
    public TimeSpan? Duration { get; set; }
}
public class AudioFileMeta : MediaFileMeta { }
public class VideoFileMeta : MediaFileMeta { }

public abstract class FileAnalysisStep {
    public DateTime CompletedUtc { get; set; }
    public string? Provider { get; set; }
    public string? Error { get; set; }
    [JsonIgnore]
    public bool Succeeded => Error == null;
}
public class FileTextExtraction : FileAnalysisStep {
    public string? Format { get; set; }
    public int Characters { get; set; }
    public bool Truncated { get; set; }
    public bool Ocr { get; set; }
    public bool Timed { get; set; }
}
public class ImageAiAnalysis : FileAnalysisStep {
    public string? Language { get; set; }
}

public sealed record ImageFocusPoint(int X, int Y);

public enum ImageObjectType {
    Face,
    Person,
    Text,
    Animal,
    Vehicle,
    Furniture,
    Food,
    Building,
    Other,
}
public class ImageObjectInfo {
    public ImageObjectType Type { get; set; } = ImageObjectType.Other;
    public string? Name { get; set; }
    public double Confidence { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public static class TranscriptTimestamps {
    const string _time = @"(\d{2,}):([0-5]\d):([0-5]\d)\.(\d{3})";
    public static Regex Pattern { get; } = new(@"^\[" + _time + " --> " + _time + @"\] ?", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    public static string Remove(string? text) => Pattern.Replace(text ?? "", "");
}
