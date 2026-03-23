namespace Relatude.DB.FileConverter;

public class FileTypeInfo {
}
public enum FileInputType {
    Image,
    Text,
    Audio,
}
public enum FileImageFormat {
    Image,
    Text,
    Audio,
}
public enum FileOutputType {
    Image,
    Text,
    Audio,
}
public interface Converter {
    Task<Stream> ConvertImage(Stream input);
}
