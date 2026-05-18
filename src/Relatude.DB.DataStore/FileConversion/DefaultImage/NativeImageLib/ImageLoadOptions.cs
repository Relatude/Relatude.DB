namespace Relatude.DB.FileConversion.DefaultImage.NativeImageLib;

public sealed record ImageLoadOptions
{
    public RectangleI? Crop { get; init; }
    public ResizeOptions? Resize { get; init; }
}
