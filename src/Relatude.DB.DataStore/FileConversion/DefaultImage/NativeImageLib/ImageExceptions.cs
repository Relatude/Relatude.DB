namespace Relatude.DB.FileConversion.DefaultImage.NativeImageLib;

public sealed class ImageFormatException : Exception
{
    public ImageFormatException(string message)
        : base(message)
    {
    }

    public ImageFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
