using Relatude.DB.Common;
using Relatude.DB.IO;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
namespace Relatude.DB.DataStores.Files;

/// <summary>What a file store stored: the hash of the bytes, the store's key for them, their length,
/// and the file id the file value must carry. The id is the one the caller asked for, except in a
/// store that keeps one copy per hash, where the bytes may turn out to be stored already.</summary>
public class FileInsertResult(string fileHash, byte[] storeKey, long length, Guid fileId) {
    public string FileHash { get; } = fileHash;
    public byte[] StoreKey { get; } = storeKey;
    public long Length { get; } = length;
    public Guid FileId { get; } = fileId;
}
/// <summary>The hash a file store computes over the bytes of every file it stores, which becomes
/// <see cref="FileValue.Hash"/> (hex). SHA256 is the stronger choice where people who are not
/// trusted can upload into a store that keeps one copy per hash, as MD5 collisions can be crafted.</summary>
public enum FileHashAlgorithm {
    MD5 = 0,
    SHA256 = 1,
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
    /// <summary>The hash the caller must compute over the parts, passed to <see cref="CompletePartialUpload"/>.</summary>
    HashAlgorithmName HashAlgorithm { get; }
    Task<byte[]> InitiatePartialUpload(Guid fileId, string fileName);
    Task AppendDataAsync(Guid fileId, byte[] fileKey, byte[] buffer, int length);
    /// <summary>Called once every part is appended, with the hash and length of the whole file. The
    /// result is what the file value must carry: a store that keeps one copy per hash may move the
    /// file, or drop it in favour of the copy it already has.</summary>
    Task<FileInsertResult> CompletePartialUpload(Guid fileId, byte[] fileKey, string fileHash, long length);
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
    /// <summary>Where the store keeps its files, compared with Equals. Stores with an equal location
    /// share one folder - two MultiFile stores on one IO provider - so they must be cleaned once, against
    /// the references of all of them: each would otherwise delete the files of the other.</summary>
    object Location { get; }
    /// <summary>Deletes every file in the store whose internal reference is not in
    /// <paramref name="validInternalReferences"/>, along with any folders left empty. The set must
    /// cover all files worth keeping when the call starts, including in-flight uploads; files created
    /// on or after <paramref name="keepFilesNewerThanUtc"/> are treated as referenced, which is what
    /// lets the call run while inserts are happening - without a cutoff nothing may be inserted while
    /// it runs, as a file inserted after the set was built is not in it and would be deleted as
    /// unreferenced. With <paramref name="countOnly"/> nothing is deleted and the result reports what
    /// a real run would have deleted. <paramref name="onProgress"/> is called as (processed, total)
    /// file counts while the store is scanned.</summary>
    Task<DeleteUnReferenceResult> DeleteUnreferenced(IReadOnlySet<string> validInternalReferences, bool countOnly = false, DateTime? keepFilesNewerThanUtc = null, Action<long, long>? onProgress = null, CancellationToken cancellationToken = default);
}