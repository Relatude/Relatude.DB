using Relatude.DB.Common;

namespace Relatude.DB.FileConverter;
public class FileConversionProgressInfo(FileConversionStatus status, int progress = 0, int remaining = -1, string? message = null) {
    public FileConversionStatus Status { get; } = status;
    public int ProgressPercentage { get; } = progress;
    public int RemainingSeconds { get; } = remaining;
    public string? Message { get; } = message;
}
public enum FileConversionStatus {
    InProgress,
    Ready,
    Unsupported,
    Error,
}
public class FileConversionResult(FileConversionProgressInfo progressInfo, Stream? output = null) {
    public FileConversionProgressInfo ProgressInfo { get; } = progressInfo;
    public Stream? Output { get; } = output;
}
public interface IFileConverter { // just the conversion, calling external services, like ai analysis or video processing
    FormatPair[] GetSupportedConversions();
    Task<bool> CancelAsync(string key);
    Task<FileConversionResult> ConvertAsync(Stream input, FileConversionInfo info, int maxWaitMs);
    Task<FileConversionProgressInfo> GetStatusAsync(FileIdWithAdjustment fileIdWithAdjustment);
    Task<Stream> GetStreamAsync(FileIdWithAdjustment fileIdWithAdjustment);
}