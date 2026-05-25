using Relatude.DB.Common;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

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
public class ConversionProgress(FileConversionProgressInfo info, Stream? output = null) {
    public Stream? Output { get; } = output;
    public FileConversionProgressInfo ProgressInfo { get; } = info;
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
    void Initialize(FileConverterLibrary library);
    bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed);
    Task<bool> CancelAsync(Guid key);
    Task<ConversionProgress> DoConvertWork(InputFileSource source, FileConversionInfo info);
    bool TryGetLiveStatus(Guid fileId, FileAdjustmentBase adj, [MaybeNullWhen(false)] out FileConversionProgressInfo status);
    byte[] CreateStatusResponse(FileFormat requestedFormat, int width, int height, List<string> text, string textColor, string fillColor);
}