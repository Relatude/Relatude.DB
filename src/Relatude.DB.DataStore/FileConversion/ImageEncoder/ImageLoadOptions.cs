namespace Relatude.DB.FileConversion.NativeImageEncoder;

public sealed record ImageLoadOptions
{
    public RectangleI? Crop { get; init; }
    public ResizeOptions? Resize { get; init; }
}
