using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.DB.FileConversion;

public class ProgressEntry(
    DateTime created,
    FileConversionProgressInfo progressInfo,
    FileConversionInfo fileInfo,
    InputFileSource inputSource) {
    public DateTime Created { get; } = created;
    public FileConversionProgressInfo ProgressInfo { get; } = progressInfo;
    public FileConversionInfo FileInfo { get; } = fileInfo;
    public InputFileSource InputSource { get; } = inputSource;
}
public class ConversionInfo(ProgressEntry entry) {
    public DateTime Created { get; } = entry.Created;
    public FileConversionProgressInfo ProgressInfo { get; set; } = entry.ProgressInfo;
    public FileConversionInfo FileInfo { get; } = entry.FileInfo;
}