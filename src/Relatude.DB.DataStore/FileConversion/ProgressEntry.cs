using Relatude.DB.Common;
using System.Diagnostics;

namespace Relatude.DB.FileConversion;

internal class ProgressEntry(
    DateTime created,
    FileConversionProgressInfo progressInfo,
    FileConversionInfo fileInfo,
    InputFileSource inputSource,
    DateTime? started,
    double? processedMs) {
    public DateTime Created { get; } = created;
    public FileConversionProgressInfo ProgressInfo { get; } = progressInfo;
    public FileConversionInfo FileInfo { get; } = fileInfo;
    public InputFileSource InputSource { get; } = inputSource;
    public DateTime? Started { get; set; } = started;
    public Stopwatch? Stopwatch { get; set; }
    public double? ProcessedMs { get; set; } = processedMs;
}
internal class InternalConversionInfo(ProgressEntry entry) {
    public Guid Key => FileInfo.IdWithAdjustment.GetKey();
    public DateTime Created { get; } = entry.Created;
    public FileConversionProgressInfo ProgressInfo { get; set; } = entry.ProgressInfo;
    public FileConversionInfo FileInfo { get; } = entry.FileInfo;
}

public enum ConversionStatus {
    Queued,
    Running,
    Completed,
    Failed,
    Canceled,
}
public class FileConversions(int completed, int failed, int canceled, int queued, int running, FileConversion[] current) {
    public int Completed { get; } = completed;
    public int Failed { get; } = failed;
    public int Canceled { get; } = canceled;
    public int Queued { get; } = queued;
    public int Running { get; } = running;
    public FileConversion[] Current { get; } = current;
}
public class FileConversion {
    public FileConversion() { }
    internal FileConversion(ProgressEntry entry, ConversionStatus status, string? desc) {
        Id = entry.FileInfo.IdWithAdjustment.GetKey();
        FileName = entry.FileInfo.FileName;
        FromFormat = entry.FileInfo.FromFormat;
        ToFormat = entry.FileInfo.ToFormat;
        FromType = FileFormatUtil.GetFileType(entry.FileInfo.FromFormat);
        ToType = FileFormatUtil.GetFileType(entry.FileInfo.ToFormat);
        Property = entry.FileInfo.IdWithAdjustment.PropertyPath;
        Created = entry.Created;
        Started = entry.Started;
        Ended = entry.Started.HasValue ? entry.Started.Value.AddMilliseconds(entry.ProcessedMs ?? 0) : null;
        ProcessedMs = entry.ProcessedMs;
        Status = status;
        ProgressPercentage = entry.ProgressInfo.ProgressPercentage;
        Description = desc;
    }
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public int ProgressPercentage { get; set; } = 0;
    public DateTime? Started { get; set; } = null;
    public FileFormat FromFormat { get; } = FileFormat.Unknown;
    public FileFormat ToFormat { get; } = FileFormat.Unknown;
    public FileType FromType { get; } = FileType.Unknown;
    public FileType ToType { get; } = FileType.Unknown;
    public PropertyPath? Property { get; } = null;
    public DateTime Created { get; } = DateTime.MinValue;
    public DateTime? Ended { get; set; } = null;
    public Double? ProcessedMs { get; set; } = null;
    public ConversionStatus Status { get; } = ConversionStatus.Queued;
    public string? Description { get; set; }
}