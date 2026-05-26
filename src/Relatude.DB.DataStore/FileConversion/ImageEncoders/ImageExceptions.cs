namespace Relatude.DB.FileConversion.ImageEncoders;

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
