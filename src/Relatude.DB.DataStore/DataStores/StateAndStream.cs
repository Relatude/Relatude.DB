using Relatude.DB.Common;
using Relatude.DB.FileConversion;
namespace Relatude.DB.DataStores;

public class StateAndStream(Stream stream, bool isTemporary, FileValue fileValue, FileFormat format) {
    public Stream Stream { get; } = stream;
    public bool IsTemporary { get; } = isTemporary;
    public FileValue FileValue { get; } = fileValue;
    public FileFormat RequestedFormat { get; } = format;
}
