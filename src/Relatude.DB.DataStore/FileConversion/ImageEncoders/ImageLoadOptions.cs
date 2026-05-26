namespace Relatude.DB.FileConversion.ImageEncoders;

public sealed record ImageLoadOptions
{
    public RectangleI? Crop { get; init; }
    public ResizeOptions? Resize { get; init; }
}
