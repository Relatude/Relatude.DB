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
    string[] getFilePathWithFileName(Guid fileId, string originalFileName) {
        // using hashing to create subfolders to avoid too many files in a single folder and end with filename
        const int folderDepth = 3;
        var hash = fileId.ToString("N"); // 32 chars
        var path = new string[folderDepth + 2];
        for (int i = 0; i < folderDepth; i++) {
            path[i] = hash.Substring(i * 2, 2);
        }
        path[folderDepth] = hash;
        path[folderDepth + 1] = originalFileName;
        return [.. _basePath, .. path];
    }
    public MultiFileStore(Guid id, IIOProviderWithFolders ioProvider, string[] basePath) {
        Id = id;
        _ioProvider = ioProvider;
        _basePath = basePath;
    }
    public Task<FileValue> InsertAsync(Stream sourceStream, string? fileName = null) {
        throw new NotImplementedException();
    }
    public Task<FileValue> InsertAsync(IReadStream sourceStream, string? fileName = null) {
        var fileId = Guid.NewGuid();
        var filePath = getFilePath(fileId, fileName);
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

