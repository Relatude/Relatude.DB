using Relatude.DB.Common;
using Relatude.DB.IO;
using System.Diagnostics.CodeAnalysis;
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
    bool TryGetLocalFilePath(FileValue value, [MaybeNullWhen(false)] out string localFilePath);
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
public class DeleteUnReferenceResult(long totalBytesDeleted, int totalFilesDeleted, int totalFoldersDeleted) {
    public long TotalBytesDeleted { get; } = totalBytesDeleted;
    public int TotalFilesDeleted { get; } = totalFilesDeleted;
    public int TotalFoldersDeleted { get; } = totalFoldersDeleted;
}
/// <summary>Optional file store capability: enumerating everything the store holds and deleting the
/// files no longer referenced. A reference is the store's internal identity of a stored file — for
/// key based stores the '/'-joined file key — and is compared case-insensitively.</summary>
public interface IFileStoreDeleteUnreferenced : IFileStore {
    Task<string> GetInternalReference(FileValue value);
    /// <summary>Deletes every file in the store whose internal reference is not in
    /// <paramref name="validInternalReferences"/>, along with any folders left empty. The set must
    /// cover all files worth keeping when the call starts, including in-flight uploads, and nothing
    /// may be inserted while it runs: a file inserted after the set was built is not in it and would
    /// be deleted as unreferenced. With <paramref name="countOnly"/> nothing is deleted and the
    /// result reports what a real run would have deleted.</summary>
    Task<DeleteUnReferenceResult> DeleteUnreferenced(IReadOnlySet<string> validInternalReferences, bool countOnly = false, CancellationToken cancellationToken = default);
}