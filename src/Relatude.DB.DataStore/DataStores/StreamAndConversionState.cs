using Relatude.DB.FileConversion;

namespace Relatude.DB.DataStores;

public class StreamAndConversionState(Stream stream, bool isReady) {
    public Stream Stream { get; } = stream;
    public bool IsReady { get; } = isReady;
}
