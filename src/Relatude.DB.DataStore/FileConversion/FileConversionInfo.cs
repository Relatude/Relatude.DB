using Relatude.DB.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.DB.FileConversion;

public class FileConversionInfo(FileIdWithAdjustment idWithAdjustment, string fileName, string hash, FileFormat format) {
    public FileIdWithAdjustment IdWithAdjustment { get; } = idWithAdjustment;
    public string FileName { get; } = fileName;
    public string Hash { get; } = hash;
    public FileFormat FromFormat { get; } = format;
    public FormatPair Formats { get; } = new FormatPair(format, idWithAdjustment.Adjustment.RequestedFormat);
}