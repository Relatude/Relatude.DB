using Relatude.DB.Common;
using Relatude.DB.FileConversion;
namespace Relatude.DB.DataStores;

public class StreamAndValue(Stream stream, FileValue fileValue) {
    public Stream Stream { get; } = stream;
    public FileValue FileValue { get; } = fileValue;
}

public class StateAndStream(Stream stream, bool isReady, FileValue fileValue, FileFormat format, Guid conversionId, FileConversionInfo? conversionInfo) {
    public Guid ConversionId { get; } = conversionId;
    public FileConversionInfo? ConversionInfo { get; } = conversionInfo;
    public Stream Stream { get; } = stream;
    public byte[] GetBytes() {
        var bytes = new byte[Stream.Length];
        Stream.Read(bytes, 0, bytes.Length);
        return bytes;
    }
    public bool IsReady { get; } = isReady;
    public FileValue FileValue { get; } = fileValue;
    public FileFormat RequestedFormat { get; } = format;
}
