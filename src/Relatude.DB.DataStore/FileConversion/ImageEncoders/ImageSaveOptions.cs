namespace Relatude.DB.FileConversion.ImageEncoders;

public sealed record ImageSaveOptions
{
    public RectangleI? Crop { get; init; }
    public ResizeOptions? Resize { get; init; }

    public int Quality { get; init; } = 90;
    public int PngCompressionLevel { get; init; } = 6;
}
