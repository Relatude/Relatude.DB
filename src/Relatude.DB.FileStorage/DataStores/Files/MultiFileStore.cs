using Relatude.DB.IO;
using Relatude.DB.Common;
using System.Security.Cryptography;
namespace Relatude.DB.DataStores.Files;

public class MultiFileStore : IDisposable, IFileStore {
    readonly IIOProviderWithFolders _ioProvider;
    readonly string[] _basePath;
    public Guid Id { get; }
    const int folderDepth = 0;
    string[] getFullPathWithBase(string[] path) {
        return [.. _basePath, .. path];
    }
    string[] getFilePath(Guid fileId, string? originalFileName, out string usedFileName) {
        var hash = fileId.ToString("N"); // 32 chars. example: "d3b07384d113edec49eaa6238ad5ff00"
        var path = new string[folderDepth + 1];
        for (int i = 0; i < folderDepth; i++) {
            path[i] = hash.Substring(i * 2, 2);
        }
        originalFileName = FileKeyUtility.FilterLegalCharInFileKey(originalFileName);
        var filenameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
        if (filenameWithoutExt == null) filenameWithoutExt = "noname";
        var fileExt = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(fileExt)) fileExt = "";
        else if (fileExt.Length > 10) fileExt = fileExt[10..];
        if (filenameWithoutExt.Length + hash.Length + 1 + fileExt.Length > FileKeyUtility.MaxFileNameLength) {
            filenameWithoutExt = filenameWithoutExt[..(FileKeyUtility.MaxFileNameLength - hash.Length - 1 - fileExt.Length)];
        }
        usedFileName = filenameWithoutExt + "." + hash + fileExt;
        path[folderDepth] = usedFileName;
        return path;
    }
    public MultiFileStore(Guid id, IIOProviderWithFolders ioProvider, string[] basePath) {
        Id = id;
        _ioProvider = ioProvider;
        _basePath = basePath;
    }
    byte[] combineHash(byte[] hash1, byte[] hash2) {
        var combined = new byte[hash1.Length + hash2.Length];
        Buffer.BlockCopy(hash1, 0, combined, 0, hash1.Length);
        Buffer.BlockCopy(hash2, 0, combined, hash1.Length, hash2.Length);
        return MD5.HashData(combined);
    }
    public async Task<FileValue> InsertAsync(Stream sourceStream, string? fileName) {
        var multiMeta = await insertAsync(sourceStream.Length, (buffer, count) => sourceStream.ReadAsync(buffer, 0, count), fileName);
        return multiMeta.ToFileValue(Id);
    }
    public async Task<FileValue> InsertAsync(IReadStream sourceStream, string? fileName) {
        var multiMeta = await insertAsync(sourceStream.Length, sourceStream.ReadAsync, fileName);
        return multiMeta.ToFileValue(Id);
    }
    public async Task<MultiStorageFileMeta> insertAsync(long length, Func<byte[], int, Task<int>> readAsync, string? fileName) {
        var fileId = Guid.NewGuid();
        var relPath = getFilePath(fileId, fileName, out var usedFileName);
        var fullPath = getFullPathWithBase(relPath);
        using var outStream = _ioProvider.OpenAppend(relPath);
        var bufferSize = 1024 * 1024; // 1MB buffer
        var buffer = new byte[bufferSize];
        long totalBytesRead = 0;
        byte[] checksum = [];
        while (true) {
            var bytesToRead = (int)Math.Min(bufferSize, length - totalBytesRead);
            var bytesRead = await readAsync(buffer, bytesToRead);
            if (bytesRead == 0) break; // End of stream            
            byte[] chk = MD5.HashData(buffer);
            checksum = combineHash(checksum, chk);
            await outStream.AppendAsyncNoChecksumOrLock(buffer, bytesRead);
            totalBytesRead += bytesRead;
        }
        if (totalBytesRead != length) throw new Exception("Length mismatch");
        return new MultiStorageFileMeta(fileId, totalBytesRead, checksum, fileName, fileName, relPath);
    }
    public async Task ExtractAsync(FileValue value, Stream outStream) {
        await extractAsync(value, (buffer, count) => outStream.WriteAsync(buffer, 0, count));
    }
    public async Task ExtractAsync(FileValue value, IAppendStream outStream) {
        await extractAsync(value, outStream.AppendAsyncNoChecksumOrLock);
    }
    public async Task<MultiStorageFileMeta> extractAsync(FileValue value, Func<byte[], int, Task> writeAsync) {
        var multiStorageFileMeta = MultiStorageFileMeta.FromFileValue(value);
        var path = getFullFilePath(multiStorageFileMeta.Id, multiStorageFileMeta.OriginalFileName);
        using var inStream = _ioProvider.OpenRead(path, 0);
        var bufferSize = 1024 * 1024; // 1MB buffer
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
        return multiStorageFileMeta;
    }
    public Task<bool> ContainsFileAsync(FileValue fileValue) {
        var multiStorageFileMeta = MultiStorageFileMeta.FromFileValue(fileValue);
        var path = getFullFilePath(multiStorageFileMeta.Id, multiStorageFileMeta.OriginalFileName);
        var fileSize = _ioProvider.GetFileSizeOrZeroIfUnknown(path);
        return Task.FromResult(fileValue.Size == fileSize);
    }
    public async Task DeleteAsync(FileValue value) {
        var multiStorageFileMeta = MultiStorageFileMeta.FromFileValue(value);
        var path = getFullFilePath(multiStorageFileMeta.Id, multiStorageFileMeta.OriginalFileName);
        _ioProvider.DeleteIfItExists(path);
    }
    public long GetSize() {
        throw new NotImplementedException();
    }
    public void Dispose() {
        _ioProvider.CloseAllOpenStreams();
    }

}

