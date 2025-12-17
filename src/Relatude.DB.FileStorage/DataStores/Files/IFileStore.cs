using Relatude.DB.Common;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Files;
public interface IFileStore : IDisposable {
    Guid Id { get; }
    Task ExtractAsync(FileValue value, Stream outStream);
    Task ExtractAsync(FileValue value, IAppendStream outStream);
    Task<FileValue> InsertAsync(Stream sourceStream, string? fileName = null);
    Task<FileValue> InsertAsync(IReadStream sourceStream, string? fileName = null);
    Task<bool> ContainsFileAsync(FileValue fileValue);
    Task DeleteAsync(FileValue value);
    Task ExtractCopy(Stream outStream);
    long GetSize();
}