using Relatude.DB.IO;
using Relatude.DB.Common;
using System.Security.Cryptography;
using System.Text;
namespace Relatude.DB.DataStores.Files;

public class MultiFileStore : IDisposable, IFileStore {
    readonly IIOProvider _ioProvider;
    readonly string[] _basePath;
    public Guid Id { get; }
    readonly int folderDepth;
    public MultiFileStore(Guid id, IIOProvider ioProvider, FileKeyUtility fileKeyUtility, int? folderDepth) {
        Id = id;
        _ioProvider = ioProvider;
        _basePath = [fileKeyUtility.MultiFileStoreFolderKey];
        this.folderDepth = folderDepth.HasValue ? folderDepth.Value : 2;
    }
    public async Task<FileInsertResult> InsertAsync(Guid newFileId, Stream sourceStream, string? fileName) {
        return await insertAsync(newFileId, sourceStream.Length, (buffer, count) => sourceStream.ReadAsync(buffer, 0, count), fileName);
    }
    public async Task<FileInsertResult> InsertAsync(Guid newFileId, IReadStream sourceStream, string? fileName) {
        return await insertAsync(newFileId, sourceStream.Length, sourceStream.ReadAsync, fileName);
    }
    public async Task<FileInsertResult> insertAsync(Guid fileId, long length, Func<byte[], int, Task<int>> readAsync, string? friendlyFileName) {
        var usedFileName = getSafeFilename(fileId, friendlyFileName);
        var fullPath = getFullPath(fileId, usedFileName);
        using var outStream = _ioProvider.OpenAppend(fullPath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
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
        var fileHash = Convert.ToHexString(hash.GetHashAndReset());
        return new FileInsertResult(fileHash, stringToBytes(usedFileName), length);
    }
    public async Task ExtractAsync(FileValue value, Stream outStream) {
        await extractAsync(value, (buffer, count) => outStream.WriteAsync(buffer, 0, count));
    }
    public async Task ExtractAsync(FileValue value, IAppendStream outStream) {
        await extractAsync(value, outStream.AppendAsyncNoChecksumOrLock);
    }
    public async Task extractAsync(FileValue value, Func<byte[], int, Task> writeAsync) {
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
    public async Task DeleteAsync(FileValue value) => _ioProvider.DeleteFileIfItExists(getFullPath(value));
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
        for(int i = 0; i < _basePath.Length; i++) path[i] = _basePath[i];
        for (int i = _basePath.Length; i < path.Length - 1; i++) path[i] = fileIdString.Substring((i - _basePath.Length) * 2, 2);
        path[^1] = safeFilename;
        return path;
    }
    static byte[] stringToBytes(string value) => Encoding.UTF8.GetBytes(value);
    static string stringFromBytes(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    public void Dispose() {
        _ioProvider.CloseAllOpenStreams();
    }
}

