using Relatude.DB.Common;
using Relatude.DB.IO;
namespace Relatude.DB.DataStores.Files;

public class FileInsertResult(string fileHash, byte[] storeKey, long length) {
    public string FileHash { get; } = fileHash;
    public byte[] StoreKey { get; } = storeKey;
    public long Length { get; } = length;
}

public interface IFileStore : IDisposable {
    Guid Id { get; }
    Task ExtractAsync(FileValue value, Stream outStream);
    Task ExtractAsync(FileValue value, IAppendStream outStream);
    Task<FileInsertResult> InsertAsync(Guid newFileId, Stream sourceStream, string? fileName = null);
    Task<FileInsertResult> InsertAsync(Guid newFileId, IReadStream sourceStream, string? fileName = null);
    Task<bool> ContainsFileAsync(FileValue fileValue);
    Task DeleteAsync(FileValue value);
    long GetSizeForMetrics();
    bool SupportsMultipartUploads() => this is IFileStoreMultiPartSupport;
}
public static class FileStoreExtensions {
    public static async Task<Stream> GetFileStream(this IFileStore fs, FileValue file) {
        var stream = new WriteToReadStream();
        _ = fs.ExtractAsync(file, stream)
            .ContinueWith(t => stream.Complete(t.IsFaulted ? t.Exception : null));
        return stream;
    }
}

public interface IFileStoreMultiPartSupport : IFileStore {
    Task<byte[]> InitiatePartialUpload(Guid fileId, string fileName);
    Task AppendDataAsync(Guid fileId, byte[] fileKey, byte[] buffer, int length);
}