namespace Relatude.DB.FileConversion.DefaultImage.NativeImageLib;

public enum ResizeKernel
{
    Nearest,
    Bilinear,
    Lanczos3
}

public sealed record ResizeOptions(int Width, int Height, ResizeKernel Kernel = ResizeKernel.Lanczos3)
{
    public void Validate()
    {
        if (Width <= 0 || Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Width), "Resize dimensions must be positive.");
        }
    }
}
