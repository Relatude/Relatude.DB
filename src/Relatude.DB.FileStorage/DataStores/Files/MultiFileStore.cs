using Relatude.DB.IO;
using Relatude.DB.Common;
using System.Text;
using System.Security.Cryptography;
namespace Relatude.DB.DataStores.Files;

public class MultiFileStore : IDisposable, IFileStore {
    object _fileLock = new();
    IAppendStream? _file;
    readonly IIOProviderWithFolders _ioProvider;
    readonly string[] _basePath;
    public Guid Id { get; }
    string[] getFullFilePath(Guid fileId, string originalFileName) {
        const int folderDepth = 3;
        var hash = fileId.ToString("N"); // 32 chars
        var path = new string[folderDepth + 2];
        for (int i = 0; i < folderDepth; i++) {
            path[i] = hash.Substring(i * 2, 2);
        }
        path[folderDepth] = hash; // remain hash
        path[folderDepth + 1] = originalFileName;
        return [.. _basePath, .. path];
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
        fileName ??= "noname";
        var filePath = getFullFilePath(fileId, fileName);
        using var outStream = _ioProvider.OpenAppend(filePath);
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
            await outStream.WriteAsync(buffer, bytesRead);
            totalBytesRead += bytesRead;
        }
        if (totalBytesRead != length) throw new Exception("Length mismatch");
        return new MultiStorageFileMeta(fileId, totalBytesRead, checksum, fileName);
    }
    public Task ExtractAsync(FileValue value, Stream outStream) {
        throw new NotImplementedException();
    }
    public Task ExtractAsync(FileValue value, IAppendStream outStream) {
        throw new NotImplementedException();
    }
    public Task<bool> ContainsFileAsync(FileValue fileValue) {
        throw new NotImplementedException();
    }
    public Task DeleteAsync(FileValue value) {
        throw new NotImplementedException();
    }
    public Task ExtractCopy(Stream outStream) {
        throw new NotImplementedException();
    }
    public long GetSize() {
        throw new NotImplementedException();
    }
    public void Dispose() {
        throw new NotImplementedException();
    }

}

