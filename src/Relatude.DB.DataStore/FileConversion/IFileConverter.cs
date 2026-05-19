using Relatude.DB.Common;

namespace Relatude.DB.FileConverter;

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
public class ConversionProgress(FileConversionProgressInfo info, Stream? output = null) {
    public Stream? Output { get; } = output;
    public FileConversionProgressInfo ProgressInfo { get; } = info;
}
public interface IFileConverter { // just the conversion,  calling local image components or external services, like ai analysis or video processing
    int MaxConcurrentWork { get; set; }
    int MinIntervalBetweenCallsInMs { get; set; }
    bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed);
    Task<bool> CancelAsync(Guid key);
    Task<ConversionProgress> DoConvertWork(Func<Task<Stream>> getInputStream, FileConversionInfo info);
    Stream GetStatusRepresentation(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status);
}