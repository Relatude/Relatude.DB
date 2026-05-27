namespace Relatude.DB.Common;

public enum FileType {
    Unknown,
    Document,
    Image,
    Video,
    Audio,
}
public enum FileFormat {
    // Image formats
    Jpeg,
    Png,
    Gif,
    Bmp,
    Svg,
    Webp,
    Avif,
    // Video formats
    Mp4,
    Avi,
    Mov,
    Wmv,
    Mkv,
    Flv,
    // Audio formats
    Mp3,
    Wav,
    Aac,
    Flac,
    // Document formats
    Pdf,
    Doc,
    Docx,
    Xls,
    Xlsx,
    Ppt,
    Pptx,
    Txt,
    // Other formats can be added here
    Unknown,
}
public static class FileFormatUtil {
    public static FileType GetFileType(string fileNameWithExtension) {
        var detailedFormat = GetDetailedFormatFromFileName(fileNameWithExtension);
        return GetFileType(detailedFormat);
    }
    public static FileFormat GetDetailedFormatFromFileName(string fileNameWithExtension) {
        var ext = Path.GetExtension(fileNameWithExtension).ToLower();
        return ext switch {
            ".jpg" or ".jpeg" => FileFormat.Jpeg,
            ".png" => FileFormat.Png,
            ".gif" => FileFormat.Gif,
            ".bmp" => FileFormat.Bmp,
            ".svg" => FileFormat.Svg,
            ".webp" => FileFormat.Webp,
            ".avif" => FileFormat.Avif,
            ".mp4" => FileFormat.Mp4,
            ".mkv" => FileFormat.Mkv,
            ".avi" => FileFormat.Avi,
            ".mov" => FileFormat.Mov,
            ".wmv" => FileFormat.Wmv,
            ".flv" => FileFormat.Flv,
            ".mp3" => FileFormat.Mp3,
            ".wav" => FileFormat.Wav,
            ".aac" => FileFormat.Aac,
            ".flac" => FileFormat.Flac,
            ".pdf" => FileFormat.Pdf,
            ".doc" => FileFormat.Doc,
            ".docx" => FileFormat.Docx,
            ".xls" => FileFormat.Xls,
            ".xlsx" => FileFormat.Xlsx,
            ".ppt" => FileFormat.Ppt,
            ".pptx" => FileFormat.Pptx,
            ".txt" => FileFormat.Txt,
            _ => FileFormat.Unknown, // This should probably be a different value or throw an exception since it's not a detailed format
        };
    }
    public static FileType GetFileType(FileFormat format) {
        return format switch {
            FileFormat.Jpeg or FileFormat.Png or FileFormat.Gif or FileFormat.Bmp or FileFormat.Svg or FileFormat.Webp or FileFormat.Avif => FileType.Image,
            FileFormat.Mp4 or FileFormat.Avi or FileFormat.Mov or FileFormat.Wmv or FileFormat.Flv or FileFormat.Mkv => FileType.Video,
            FileFormat.Mp3 or FileFormat.Wav or FileFormat.Aac or FileFormat.Flac => FileType.Audio,
            FileFormat.Pdf or FileFormat.Doc or FileFormat.Docx or FileFormat.Xls or FileFormat.Xlsx or FileFormat.Ppt or FileFormat.Pptx or FileFormat.Txt => FileType.Document,
            FileFormat.Unknown => FileType.Unknown,
            _ => throw new Exception("Internal error. Unhandled FileFormat value: " + format.ToString()), // This should never happen if all FileFormat values are covered
        };
    }

    public static string GetContentType(FileFormat fileFormat) => fileFormat switch {
        FileFormat.Jpeg => "image/jpeg",
        FileFormat.Png => "image/png",
        FileFormat.Gif => "image/gif",
        FileFormat.Bmp => "image/bmp",
        FileFormat.Svg => "image/svg+xml",
        FileFormat.Webp => "image/webp",
        FileFormat.Avif => "image/avif",
        FileFormat.Mp4 => "video/mp4",
        FileFormat.Mkv => "video/x-matroska",
        FileFormat.Avi => "video/x-msvideo",
        FileFormat.Mov => "video/quicktime",
        FileFormat.Wmv => "video/x-ms-wmv",
        FileFormat.Flv => "video/x-flv",
        FileFormat.Mp3 => "audio/mpeg",
        FileFormat.Wav => "audio/wav",
        FileFormat.Aac => "audio/aac",
        FileFormat.Flac => "audio/flac",
        FileFormat.Pdf => "application/pdf",
        FileFormat.Doc => "application/msword",
        FileFormat.Docx => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        FileFormat.Xls => "application/vnd.ms-excel",
        FileFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        FileFormat.Ppt => "application/vnd.ms-powerpoint",
        FileFormat.Pptx => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        FileFormat.Txt => "text/plain",
        _ => "application/octet-stream",
    };

    public static string? GetExtensionWithDot(FileFormat fmt) {
        return fmt switch {
            FileFormat.Jpeg => ".jpeg",
            FileFormat.Png => ".png",
            FileFormat.Gif => ".gif",
            FileFormat.Bmp => ".bmp",
            FileFormat.Svg => ".svg",
            FileFormat.Webp => ".webp",
            FileFormat.Avif => ".avif",
            FileFormat.Mp4 => ".mp4",
            FileFormat.Mkv => ".mkv",
            FileFormat.Avi => ".avi",
            FileFormat.Mov => ".mov",
            FileFormat.Wmv => ".wmv",
            FileFormat.Flv => ".flv",
            FileFormat.Mp3 => ".mp3",
            FileFormat.Wav => ".wav",
            FileFormat.Aac => ".aac",
            FileFormat.Flac => ".flac",
            FileFormat.Pdf => ".pdf",
            FileFormat.Doc => ".doc",
            FileFormat.Docx => ".docx",
            FileFormat.Xls => ".xls",
            FileFormat.Xlsx => ".xlsx",
            FileFormat.Ppt => ".ppt",
            FileFormat.Pptx => ".pptx",
            FileFormat.Txt => ".txt",
            _ => null,
        };
    }
}
public struct FormatPair : IEquatable<FormatPair> {
    public FormatPair(FileFormat from, FileFormat to) {
        From = from;
        To = to;
    }

    public FileFormat From { get; }
    public FileFormat To { get; }

    // Strongly-typed equality (no boxing)
    public bool Equals(FormatPair other) {
        return From.Equals(other.From) && To.Equals(other.To);
    }

    // Object override (required)
    public override bool Equals(object? obj) {
        return obj is FormatPair other && Equals(other);
    }

    // Hash code (important for dictionaries, sets, etc.)
    public override int GetHashCode() {
        return HashCode.Combine(From, To);
    }

    // Operator overloads
    public static bool operator ==(FormatPair left, FormatPair right) {
        return left.Equals(right);
    }

    public static bool operator !=(FormatPair left, FormatPair right) {
        return !(left == right);
    }
}