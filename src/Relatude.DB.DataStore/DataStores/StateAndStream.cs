using Relatude.DB.Common;
namespace Relatude.DB.DataStores;

public class StateAndStream(Stream stream, bool isReady, FileValue fileValue, FileFormat format, Guid conversionId) {
    public Guid ConversionId { get; } = conversionId;
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
