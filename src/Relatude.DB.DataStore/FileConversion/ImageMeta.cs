using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relatude.DB.FileConversion;

public enum ImageCropMode {
    Fill, // Resize and crop to fill the target dimensions. This may cut off parts of the image if the aspect ratio doesn't match.
    Fit, // Resize to fit within the target dimensions while preserving aspect ratio. This may result in letterboxing (empty space) if the aspect ratio doesn't match.
    Stretch, // Resize to fill the target dimensions without preserving aspect ratio. This may distort the image if the aspect ratio doesn't match.
    Auto, // Automatically choose between Fill and Fit based on the content of the image. For example, if the image has a mostly uniform colored edge, it should use Fit.
}
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
    //public string? ObjectName { get; set; }
    public string? ObjectText { get; set; }
    public double Confidence { get; set; }
    public ImageObjectType ObjectType { get; set; } = ImageObjectType.Other;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
public class ImageMeta {
    public string? Photographer { get; set; }
    public string? Copyright { get; set; }
    public double? GPSLatitude { get; set; }
    public double? GPSLongitude { get; set; }
    public string? AiDescription { get; set; }
    public string? AiKeywords { get; set; }
    public ImageCropMode? DefaultCropMode { get; set; }
    public int? FocusPointX { get; set; }
    public int? FocusPointY { get; set; }
    public ImageObjectInfo[]? DetectedObjects { get; set; }
    public string? GetAllText() {
        if (DetectedObjects == null) return null;
        return string.Join(Environment.NewLine, DetectedObjects.Where(o => !string.IsNullOrEmpty(o.ObjectText)).Select(o => o.ObjectText));
    }
    static JsonSerializerOptions _options = new() {
        WriteIndented = false,
        // DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
    public Stream ToStream() {
        var json = JsonSerializer.Serialize(this, _options);
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
    }
    public static ImageMeta FromStream(Stream stream) {
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<ImageMeta>(json, _options) ?? new ImageMeta();
    }
}
