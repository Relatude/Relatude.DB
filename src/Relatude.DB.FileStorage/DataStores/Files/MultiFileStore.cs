using Relatude.DB.Common;
using Relatude.DB.IO;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
namespace Relatude.DB.DataStores.Files;

/// <summary>
/// One file per stored file, under folders taken from the file id. With <c>sameHashSameFile</c> the
/// store keeps one copy of any content: an upload is written as usual, then moved to a place named
/// after its hash - or dropped, when a file with that hash and length is already there - and the file
/// value is given the hash based id and name. Such files can be shared by many file values, so only
/// <see cref="DeleteUnreferenced"/> removes them. Files stored before the option was turned on stay
/// where they are, and files kept by their hash stay readable after it is turned off.
/// </summary>
public class MultiFileStore : IDisposable, IFileStore, IFileStoreMultiPartSupport, IFileStoreDeleteUnreferenced {
    readonly IIOProvider _ioProvider;
    readonly string[] _basePath;
    public Guid Id { get; }
    readonly int folderDepth;
    public HashAlgorithmName HashAlgorithm { get; }
    readonly bool _sameHashSameFile;
    // only with sameHashSameFile: a lock per stripe of file keys, so a second upload of the same bytes
    // and the unreferenced sweep deleting them wait for each other while other uploads do not
    readonly object[]? _hashLocks;
    // only with sameHashSameFile: when an upload was last given each file kept by its hash. A file that
    // was unreferenced when a sweep collected its references may be in use by the time it gets there.
    readonly ConcurrentDictionary<string, DateTime>? _handedOut;
    // how long a hand-out is remembered: longer than the grace period any sweep is given
    static readonly TimeSpan _handedOutMemory = TimeSpan.FromHours(1);
    DateTime _nextHandedOutPrune = DateTime.MinValue;
    public MultiFileStore(Guid id, IIOProvider ioProvider, int? folderDepth, bool sameHashSameFile = false, FileHashAlgorithm hashAlgorithm = FileHashAlgorithm.MD5) {
        Id = id;
        _ioProvider = ioProvider;
        _basePath = [FileKeyUtility.MultiFileStoreFolderKey];
        this.folderDepth = folderDepth.HasValue ? folderDepth.Value : 2;
        HashAlgorithm = hashAlgorithm switch {
            FileHashAlgorithm.MD5 => HashAlgorithmName.MD5,
            FileHashAlgorithm.SHA256 => HashAlgorithmName.SHA256,
            _ => throw new ArgumentOutOfRangeException(nameof(hashAlgorithm), hashAlgorithm, "Unknown file hash algorithm. "),
        };
        _sameHashSameFile = sameHashSameFile;
        if (sameHashSameFile) {
            _hashLocks = new object[64];
            for (var i = 0; i < _hashLocks.Length; i++) _hashLocks[i] = new();
            _handedOut = new(StringComparer.OrdinalIgnoreCase);
        }
    }
    public async Task<FileInsertResult> InsertAsync(Guid newFileId, Stream sourceStream, string? fileName) {
        return await insertAsync(newFileId, sourceStream.Length, (buffer, count) => sourceStream.ReadAsync(buffer, 0, count), fileName);
    }
    public async Task<FileInsertResult> InsertAsync(Guid newFileId, IReadStream sourceStream, string? fileName) {
        return await insertAsync(newFileId, sourceStream.Length, sourceStream.ReadAsync, fileName);
    }
    async Task<FileInsertResult> insertAsync(Guid fileId, long length, Func<byte[], int, Task<int>> readAsync, string? friendlyFileName) {
        var usedFileName = getSafeFilename(fileId, friendlyFileName);
        var fullPath = getFullPath(fileId, usedFileName);
        string fileHash;
        using (var outStream = _ioProvider.OpenAppend(fullPath)) { // closed before the file can be moved
            using var hash = IncrementalHash.CreateHash(HashAlgorithm);
            var bufferSize = 1024 * 1024; // 1MB buffer
            bufferSize = length < bufferSize ? (int)length : bufferSize;
            var buffer = new byte[bufferSize];
            long totalBytesRead = 0;
            while (true) {
                var bytesToRead = (int)Math.Min(bufferSize, length - totalBytesRead);
                var bytesRead = await readAsync(buffer, bytesToRead);
                if (bytesRead == 0) break; // End of stream
                hash.AppendData(buffer, 0, bytesRead);
                await outStream.AppendAsyncNoChecksumOrLock(buffer, bytesRead);
                totalBytesRead += bytesRead;
            }
            if (totalBytesRead != length) throw new Exception("Length mismatch");
            fileHash = Convert.ToHexString(hash.GetHashAndReset());
        }
        if (_sameHashSameFile) return keepOneCopy(fileId, usedFileName, fileHash, length);
        return new FileInsertResult(fileHash, stringToBytes(usedFileName), length, fileId);
    }
    // A file kept by its hash has the first 128 bits of the hash as its file id, so its folders come
    // from the hash too, and the rest of the hash and the length as its name. Both follow from the
    // bytes alone, which is how a second upload of the same bytes finds the first. The name is lower
    // case as file keys must be, and the full hash can be read back from the folders and the name.
    static Guid fileIdOfHash(string fileHash) => Guid.ParseExact(fileHash.AsSpan(0, 32), "N");
    string fileNameOfHash(string fileHash, long length) => fileHash[(folderDepth * 2)..].ToLowerInvariant() + "-" + length;
    // Moves a file just written under its upload id to the place named after its hash, or deletes it
    // when the same content is there already, and returns what the file value must point at.
    FileInsertResult keepOneCopy(Guid uploadedFileId, string uploadedFileName, string fileHash, long length) {
        var fileId = fileIdOfHash(fileHash);
        var fileName = fileNameOfHash(fileHash, length);
        var uploadedPath = getFullPath(uploadedFileId, uploadedFileName);
        var path = getFullPath(fileId, fileName);
        var key = path.AsKeyString();
        lock (hashLockOf(key)) {
            var alreadyStored = length == 0 ? _ioProvider.Exists(path) : _ioProvider.GetFileSizeOrZeroIfUnknown(path) == length;
            if (alreadyStored) {
                _ioProvider.DeleteFileIfItExists(uploadedPath);
            } else {
                _ioProvider.DeleteFileIfItExists(path); // a copy of the wrong length, left by an interrupted copy
                if (_ioProvider.CanRenameFile) {
                    _ioProvider.RenameFile(uploadedPath, path);
                } else { // blob storage: copied, so a new file is written twice there
                    _ioProvider.CopyFile(uploadedPath, path);
                    _ioProvider.DeleteFileIfItExists(uploadedPath);
                }
            }
            var now = DateTime.UtcNow;
            _handedOut![key] = now;
            pruneHandedOut(now);
        }
        return new FileInsertResult(fileHash, stringToBytes(fileName), length, fileId);
    }
    object hashLockOf(string key) => _hashLocks![(StringComparer.OrdinalIgnoreCase.GetHashCode(key) & int.MaxValue) % _hashLocks.Length];
    void pruneHandedOut(DateTime now) {
        if (now < _nextHandedOutPrune) return; // at most once a minute, so a bulk import is not slowed down
        _nextHandedOutPrune = now.AddMinutes(1);
        var forgetBefore = now - _handedOutMemory;
        foreach (var entry in _handedOut!) {
            if (entry.Value < forgetBefore) _handedOut.TryRemove(entry); // only if not handed out again since
        }
    }
    public async Task ExtractAsync(FileValue value, Stream outStream) {
        await extractAsync(value, (buffer, count) => outStream.WriteAsync(buffer, 0, count));
    }
    public async Task ExtractAsync(FileValue value, IAppendStream outStream) {
        await extractAsync(value, outStream.AppendAsyncNoChecksumOrLock);
    }
    async Task extractAsync(FileValue value, Func<byte[], int, Task> writeAsync) {
        var path = getFullPath(value);
        using var inStream = _ioProvider.OpenRead(path, 0);
        var bufferSize = 5 * 1024 * 1024; // 5MB buffer
        var buffer = new byte[bufferSize];
        long bytesRead = 0;
        while (true) {
            var bytesToRead = (int)Math.Min(bufferSize, inStream.Length - bytesRead);
            if (bytesToRead <= 0) break;
            var read = await inStream.ReadAsync(buffer, bytesToRead);
            if (read == 0) break;
            await writeAsync(buffer, read);
            bytesRead += read;
        }
    }
    public Task<bool> ContainsFileAsync(FileValue fileValue) => Task.FromResult(fileValue.Size == _ioProvider.GetFileSizeOrZeroIfUnknown(getFullPath(fileValue)));
    public async Task DeleteAsync(FileValue value) {
        // a file kept by its hash may be shared by other file values, so it is left for the unreferenced
        // sweep - checked whether or not the store keeps one copy per hash now, as it may have once
        if (FileValue.IsKeptByHash(value)) return;
        _ioProvider.DeleteFileIfItExists(getFullPath(value));
    }
    public long GetSizeForMetrics() {
        return 0; // not implemented. Scanning could be expensive, so we return 0 for now. 
    }

    string getSafeFilename(Guid fileId, string? originalFileName) {
        var fileIdString = fileId.ToString("N");
        originalFileName = FileKeyUtility.FilterLegalCharInFileKey(originalFileName);
        var filenameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
        if (filenameWithoutExt == null) filenameWithoutExt = "noname";
        var fileExt = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(fileExt)) fileExt = "";
        else if (fileExt.Length > 10) fileExt = fileExt[10..];
        if (filenameWithoutExt.Length + fileIdString.Length + 1 + fileExt.Length > FileKeyUtility.MaxFileNameLength) {
            filenameWithoutExt = filenameWithoutExt[..(FileKeyUtility.MaxFileNameLength - fileIdString.Length - 1 - fileExt.Length)];
        }
        var fileIdStringWithoutPartsUsedForFolders = fileIdString[(folderDepth * 2)..];
        var usedFilename = filenameWithoutExt + "." + fileIdStringWithoutPartsUsedForFolders + fileExt;
        return usedFilename;
    }
    string[] getFullPath(FileValue value) {
        var safeFileName = stringFromBytes(FileValue.GetFileKeyData(value));
        return getFullPath(value.FileId, safeFileName);
    }
    string[] getFullPath(Guid fileId, string safeFilename) {
        // use fileId to create subfolders to avoid too many files in one folder, which can cause performance issues in some file systems
        // example: if folderDepth is 2 and fileId is "12345678-1234-1234-1234-1234567890" and 
        // originalFileName is "myfile.txt", the path will be "basePath/12/34/myfile.56781234123412341234567890.txt"
        var fileIdString = fileId.ToString("N");
        var path = new string[_basePath.Length + folderDepth + 1];
        for (int i = 0; i < _basePath.Length; i++) path[i] = _basePath[i];
        for (int i = _basePath.Length; i < path.Length - 1; i++) path[i] = fileIdString.Substring((i - _basePath.Length) * 2, 2);
        path[^1] = safeFilename;
        return path;
    }
    static byte[] stringToBytes(string value) => Encoding.UTF8.GetBytes(value);
    static string stringFromBytes(byte[] bytes) => Encoding.UTF8.GetString(bytes);
    public void Dispose() {
        _ioProvider.CloseAllOpenStreams();
    }

    public async Task<byte[]> InitiatePartialUpload(Guid fileId, string fileName) {
        var usedFileName = getSafeFilename(fileId, fileName);
        var fullPath = getFullPath(fileId, usedFileName);
        using var outStream = _ioProvider.OpenAppend(fullPath); // create file
        return stringToBytes(usedFileName);
    }
    public async Task AppendDataAsync(Guid fileId, byte[] fileKey, byte[] buffer, int length) {
        var usedFileName = stringFromBytes(fileKey);
        var fullPath = getFullPath(fileId, usedFileName);
        using var outStream = _ioProvider.OpenAppend(fullPath);
        await outStream.AppendAsyncNoChecksumOrLock(buffer, length);
    }
    public Task<FileInsertResult> CompletePartialUpload(Guid fileId, byte[] fileKey, string fileHash, long length) {
        if (!_sameHashSameFile) return Task.FromResult(new FileInsertResult(fileHash, fileKey, length, fileId));
        var usedFileName = stringFromBytes(fileKey);
        // the name it is moved to carries the length, so the parts must all be there
        var stored = _ioProvider.GetFileSizeOrZeroIfUnknown(getFullPath(fileId, usedFileName));
        if (stored != length) throw new Exception("The uploaded file is " + stored + " bytes, expected " + length + ". ");
        return Task.FromResult(keepOneCopy(fileId, usedFileName, fileHash, length));
    }
    public bool TryGetLocalFilePath(FileValue value, [MaybeNullWhen(false)] out string localFilePath) {
        var path = getFullPath(value);
        return _ioProvider.TryGetLocalFilePath(path, out localFilePath);
    }

    public Task<string> GetInternalReference(FileValue value) => Task.FromResult(getFullPath(value).AsKeyString());
    public async Task<DeleteUnReferenceResult> DeleteUnreferenced(IReadOnlySet<string> validInternalReferences, bool countOnly = false, DateTime? keepFilesNewerThanUtc = null, Action<long, long>? onProgress = null, CancellationToken cancellationToken = default) {
        // rebuilt with the file key comparer, as the caller's set may compare ordinally
        var valid = new HashSet<string>(validInternalReferences, StringComparer.OrdinalIgnoreCase);
        var root = await _ioProvider.GetFolderAsync(_basePath, true, true);
        long bytesDeleted = 0;
        int filesDeleted = 0;
        int foldersDeleted = 0;
        long totalFiles = 0;
        long processedFiles = 0;
        void countFiles(FolderMeta folder) {
            totalFiles += folder.Files.Length;
            foreach (var subFolder in folder.SubFolders) countFiles(subFolder);
        }
        countFiles(root);
        // A folder found empty in the listing may have got a file since - an upload writes into a folder
        // named by its file id - so it is only removed if it is empty at that moment, never recursively.
        // Virtual folders (blob storage, memory) go with their last file, so there is nothing to remove.
        bool removeEmptyFolder(string[] folderPath) {
            if (countOnly || !_ioProvider.SupportsEmptyFolders) return true;
            return _ioProvider.DeleteFolderIfEmpty(folderPath);
        }
        // depth first: unreferenced files first, then any subfolder left empty. A folder reports itself
        // empty instead of deleting itself so the parent can take it; the store root is always kept.
        bool deleteIn(FolderMeta folder, string[] folderPath) {
            cancellationToken.ThrowIfCancellationRequested();
            var empty = true;
            foreach (var subFolder in folder.SubFolders) {
                string[] subFolderPath = [.. folderPath, subFolder.Name];
                if (deleteIn(subFolder, subFolderPath) && removeEmptyFolder(subFolderPath)) {
                    foldersDeleted++;
                } else {
                    empty = false;
                }
            }
            foreach (var file in folder.Files) {
                cancellationToken.ThrowIfCancellationRequested();
                processedFiles++;
                onProgress?.Invoke(processedFiles, totalFiles);
                var keep = valid.Contains(file.Key) || (keepFilesNewerThanUtc.HasValue && file.CreationTimeUtc >= keepFilesNewerThanUtc.Value);
                if (keep) { empty = false; continue; }
                if (_sameHashSameFile) {
                    // checked and deleted under the lock an upload takes to reuse the file, so the
                    // upload either finds it gone or is seen here as having been given it
                    lock (hashLockOf(file.Key)) {
                        if (keepFilesNewerThanUtc.HasValue && _handedOut!.TryGetValue(file.Key, out var handedOutUtc) && handedOutUtc >= keepFilesNewerThanUtc.Value) { empty = false; continue; }
                        if (!countOnly) _ioProvider.DeleteFileIfItExists(file.KeyOf());
                    }
                } else if (!countOnly) {
                    _ioProvider.DeleteFileIfItExists(file.KeyOf());
                }
                bytesDeleted += file.Size;
                filesDeleted++;
            }
            return empty;
        }
        deleteIn(root, _basePath);
        return new DeleteUnReferenceResult(bytesDeleted, filesDeleted, foldersDeleted);
    }
}

