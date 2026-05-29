using Relatude.DB.Common;
using Relatude.DB.IO;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.FileConversion;

internal class FileConversionCache {
    readonly IIOProvider _io;
    readonly string _baseFolder;
    public FileConversionCache(IIOProvider io, string baseFolder) {
        _io = io;
        _baseFolder = baseFolder;
    }
    const int _folderDepth = 3;
    readonly Cache<Guid, byte[]> _smallCache = new(100 * 1024 * 1024); // 100mb
    int _smallFileSizeLimit = 200 * 1024; // 200kb
    string[] getFilePath(Guid key) {
        var keyString = key.ToString();
        var path = new string[_folderDepth + 1];
        path[0] = _baseFolder;
        for (int i = 0; i < _folderDepth - 1; i++) path[i + 1] = keyString.Substring(i * 2, 2);
        path[_folderDepth] = keyString;
        return path;
    }
    string[] getFilePathErrorStatus(Guid key) {
        var path = getFilePath(key);
        path[^1] += ".status";
        return path;
    }
    public bool TryGetResult(Guid key, [MaybeNullWhen(false)] out FileConversionResult result) {
        if (_smallCache.TryGet(key, out var smallData)) {
            result = new(new(FileConversionStatus.Ready, 100, 0, null), new MemoryStream(smallData));
            return true;
        }
        var path = getFilePath(key);
        if (_io.Exists(path)) {
            var stream = _io.OpenRead(path, 0).AsStream();
            var length = stream.Length;
            if (length <= _smallFileSizeLimit) {
                var buffer = new byte[length];
                stream.Read(buffer, 0, (int)length);
                stream.Dispose();
                _smallCache.Set(key, buffer, (int)length);
                result = new(new(FileConversionStatus.Ready, 100, 0, null), new MemoryStream(buffer));
            } else {
                result = new(new(FileConversionStatus.Ready, 100, 0, null), stream);
            }
            return true;
        }
        var pathError = getFilePathErrorStatus(key);
        if (_io.Exists(pathError)) {
            var errorMessage = _io.ReadString(pathError, "Error");
            result = new(new(FileConversionStatus.Error, 0, 0, errorMessage), null);
            return true;
        }
        result = null;
        return false;
    }
    public Task SetFromFileAsync(FileIdWithAdjustment fileKey, string localFilePath) {
        var filePath = getFilePath(fileKey.GetKey());
        if (_io.TryMoveIfSameDrive(localFilePath, filePath)) return Task.CompletedTask;
        var stream = File.OpenRead(localFilePath);
        return SetFromStreamAsync(fileKey, stream);
    }
    public async Task SetFromStreamAsync(FileIdWithAdjustment fileKey, Stream input) {
        var filePath = getFilePath(fileKey.GetKey());
        using var output = _io.OpenAppend(filePath);
        long bufferSize = 1024 * 1024;
        bufferSize = Math.Min(bufferSize, input.CanSeek ? input.Length : bufferSize);
        var isSmallFile = input.CanSeek ? input.Length <= _smallFileSizeLimit : false;
        var smallFileData = (isSmallFile) ? new MemoryStream((int)input.Length) : null;
        var buffer = new byte[bufferSize];
        while (true) {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            await output.AppendAsyncNoChecksumOrLock(buffer, read);
            if (smallFileData != null) smallFileData.Write(buffer, 0, read);
        }
        input.Dispose();
        output.Dispose();
        if (smallFileData != null) _smallCache.Set(fileKey.GetKey(), smallFileData.ToArray(), (int)smallFileData.Length);
    }
    public void Clear(Guid key) {
        var path = getFilePath(key);
        _io.DeleteFileIfItExists(path);
        var pathError = getFilePathErrorStatus(key);
        _io.DeleteFileIfItExists(pathError);
        _smallCache.Clear_EvenIf0Size(key);
    }
    public void ClearAll() {
        var folders = _io.GetFoldersAsync(new[] { _baseFolder }, true, true).Result;
        foreach (var folder in folders) {
            if (folder.Name == _baseFolder) continue;
            _io.DeleteFolderIfItExists(new[] { folder.Name });
        }
        _smallCache.ClearAll_NotSize0();
    }
    public void SaveErrorStatus(Guid key, string errorMessage) {
        var path = getFilePath(key);
        _io.DeleteFileIfItExists(path);
        var pathError = getFilePathErrorStatus(key);
        _io.DeleteFileIfItExists(pathError);
        _io.WriteString(pathError, errorMessage);
    }
    public void ClearAllErrors() {
        var folders = _io.GetFoldersAsync(new[] { _baseFolder }, true, true).Result;
        foreach (var folder in folders) {
            if (folder.Name == _baseFolder) continue;
            var pathError = getFilePathErrorStatus(Guid.Parse(folder.Name));
            var errorFolder = new string[pathError.Length - 1];
            if (_io.Exists(pathError)) _io.DeleteFolderIfItExists(errorFolder);
        }
    }
}
