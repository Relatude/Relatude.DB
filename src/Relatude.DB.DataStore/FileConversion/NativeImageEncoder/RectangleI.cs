namespace Relatude.DB.FileConversion.NativeImageEncoder;

public readonly record struct RectangleI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public void ValidateInside(int width, int height)
    {
        if (Width <= 0 || Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Width), "Crop rectangle must have positive dimensions.");
        }

        if (X < 0 || Y < 0 || Right > width || Bottom > height)
        {
            throw new ArgumentOutOfRangeException(nameof(X), "Crop rectangle must be inside the image bounds.");
        }
    }
}
