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
        // sized from Length, so the whole stream is expected: a partial read would leave the tail
        // of the buffer as zeroes and hand back a silently truncated file
        Stream.ReadExactly(bytes, 0, bytes.Length);
        return bytes;
    }
    public bool IsReady { get; } = isReady;
    public FileValue FileValue { get; } = fileValue;
    public FileFormat RequestedFormat { get; } = format;
}
