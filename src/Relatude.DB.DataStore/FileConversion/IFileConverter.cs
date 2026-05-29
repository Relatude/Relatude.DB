using Relatude.DB.Common;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.FileConversion;

public class FileConversionProgressInfo(FileConversionStatus status = FileConversionStatus.InProgress, int progress = 0, int remaining = -1, string? message = null) {
    public FileConversionStatus Status { get; } = status;
    public int ProgressPercentage { get; } = progress;
    public int RemainingSeconds { get; } = remaining;
    public string? Message { get; } = message;
}
public enum FileConversionStatus {
    InProgress,
    Ready,
    //Unsupported,
    Error,
}
public class FileConversionResult(FileConversionProgressInfo progressInfo, Stream? output = null) {
    public FileConversionProgressInfo ProgressInfo { get; } = progressInfo;
    public Stream? Output { get; } = output;
}
public class ConversionProgress(FileConversionProgressInfo info, Stream? output = null, string? localFilePathOutput = null) {
    public FileConversionProgressInfo ProgressInfo { get; } = info;
    public Stream? Output { get; } = output;
    public string? LocalFilePathOutput { get; } = localFilePathOutput;
}
public class InputFileSource(Func<Task<Stream>> getInputStream, string? localFilePath) {
    public Task<Stream> OpenInputStream() {
        return getInputStream();
    }
    public bool HasLocalFilePath => !string.IsNullOrEmpty(localFilePath);
    public string GetLocalFilePathOrThrow() {
        if (string.IsNullOrEmpty(localFilePath)) throw new Exception("No local file path available");
        return localFilePath;
    }
}
public interface IFileConverter { // just the conversion,  calling local image components or external services, like ai analysis or video processing
    int ThreadCount { get; set; }
    int CallDelayMs { get; set; }
    void Initialize(FileConversionEngine conversionEngine);
    bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed);
    Task<bool> CancelAsync(Guid key);
    Task<ConversionProgress> DoConvertWork(InputFileSource source, FileConversionInfo info);
    bool TryGetLiveStatus(Guid fileId, FileAdjustment adj, [MaybeNullWhen(false)] out FileConversionProgressInfo status);
    byte[] CreateStatusResponse(FileFormat requestedFormat, int width, int height, List<string> text, string textColor, string fillColor);
}
public static class IFileConverterExt {
    static public bool SupportsConversion(this IFileConverter converter, FileFormat inFormat, FileFormat outFormat) {
        return converter.SupportsConversion(FileFormatUtil.GetFileType(inFormat), inFormat, FileFormatUtil.GetFileType(outFormat), outFormat);
    }
}
