using System.Text.Json;
using System.Text.Json.Serialization;

namespace Relatude.DB.FileConverter;

public enum ImageCropMode {
    Fill,
    Fit,
    Auto,
    None,
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
