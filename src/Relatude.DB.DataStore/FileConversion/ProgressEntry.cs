using Relatude.DB.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.DB.FileConversion;

internal class ProgressEntry(
    DateTime created,
    FileConversionProgressInfo progressInfo,
    FileConversionInfo fileInfo,
    InputFileSource inputSource) {
    public DateTime Created { get; } = created;
    public FileConversionProgressInfo ProgressInfo { get; } = progressInfo;
    public FileConversionInfo FileInfo { get; } = fileInfo;
    public InputFileSource InputSource { get; } = inputSource;
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
    Failed
}
public class Conversion {
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public FileFormat FromFormat { get; } = FileFormat.Unknown;
    public FileFormat ToFormat { get; } = FileFormat.Unknown;
    public FileType FromType { get; } = FileType.Unknown;
    public FileType ToType { get; } = FileType.Unknown;
    public PropertyPath? Property { get; } = null;
    public DateTime Created { get; } = DateTime.MinValue;
    public ConversionStatus Status { get; } = ConversionStatus.Queued;
    public string? Message { get; } = null;
}