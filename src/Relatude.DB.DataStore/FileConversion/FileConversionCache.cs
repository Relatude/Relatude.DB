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
    /// <summary>The folder below the cache base folder that conversions in progress write their
    /// output to. It is not part of the cache: it is emptied when the store opens and left alone
    /// when the cache is cleared, as a running conversion may be writing in it.</summary>
    public const string TempFolderName = "temp";
    const string _errorStatusExtension = ".status";
    /// <summary>The extension of a cached file whose requested format has none of its own.</summary>
    const string _unknownFormatExtension = ".bin";
    const int _folderDepth = 3;
    readonly Cache<Guid, byte[]> _smallCache = new(100 * 1024 * 1024); // 100mb
    int _smallFileSizeLimit = 200 * 1024; // 200kb, files bigger than this will always be read from disk
    /// <summary>
    /// The cached file of one conversion: the key guid, sharded over two folder levels, carrying
    /// the extension of the format it was converted to. The extension is safe to keep in the name
    /// because the requested format is part of the key guid (every adjustment writes it into its
    /// string key first), so the name can never disagree with the bytes in the file.
    /// </summary>
    string[] getFilePath(FileIdWithAdjustment id) => getFilePath(id.GetKey(), extensionOf(id.Adjustment.RequestedFormat));
    string[] getFilePathErrorStatus(Guid key) => getFilePath(key, _errorStatusExtension);
    string[] getFilePath(Guid key, string extension) {
        var keyString = key.ToString();
        var path = new string[_folderDepth + 1];
        path[0] = _baseFolder;
        for (int i = 0; i < _folderDepth - 1; i++) path[i + 1] = keyString.Substring(i * 2, 2);
        path[_folderDepth] = keyString + extension;
        return path;
    }
    static string extensionOf(FileFormat format) => FileFormatUtil.GetExtensionWithDot(format) ?? _unknownFormatExtension;
    /// <summary>Whether the folder is one of the two levels the keys are sharded over, named by
    /// the leading hex chars of the key. Everything else below the base folder is not cache
    /// content: <see cref="TempFolderName"/> is the one such folder today.</summary>
    static bool isShardFolder(string name) => name.Length == 2 && name.All(char.IsAsciiHexDigit);


    public bool TryGetStatusNoStream(FileIdWithAdjustment id, [MaybeNullWhen(false)] out FileConversionProgressInfo progress) {
        var key = id.GetKey();
        if (_smallCache.TryGet(key, out _)) {
            progress = new(FileConversionStatus.Ready, 100, 0, null);
            return true;
        }
        if (_io.Exists(getFilePath(id))) {
            progress = new(FileConversionStatus.Ready, 100, 0, null);
            return true;
        }
        var pathError = getFilePathErrorStatus(key);
        if (_io.Exists(pathError)) {
            var errorMessage = _io.ReadString(pathError, "Error");
            progress = new(FileConversionStatus.Error, 0, 0, errorMessage);
            return true;
        }
        progress = null;
        return false;
    }

    public bool TryGetResultAndStream(FileIdWithAdjustment id, [MaybeNullWhen(false)] out FileConversionResultAndStream result) {
        var key = id.GetKey();
        if (_smallCache.TryGet(key, out var smallData)) {
            result = new(new(FileConversionStatus.Ready, 100, 0, null), new MemoryStream(smallData));
            return true;
        }
        var path = getFilePath(id);
        if (_io.Exists(path)) {
            var stream = _io.OpenRead(path, 0).AsStream();
            var length = stream.Length;
            if (length <= _smallFileSizeLimit) {
                var buffer = new byte[length];
                stream.ReadExactly(buffer, 0, (int)length);
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
        var filePath = getFilePath(fileKey);
        if (_io.TryMoveIfSameDrive(localFilePath, filePath)) 
            return Task.CompletedTask;
        var stream = File.OpenRead(localFilePath);
        return SetFromStreamAsync(fileKey, stream);
    }
    public async Task SetFromStreamAsync(FileIdWithAdjustment fileKey, Stream input) {
        var filePath = getFilePath(fileKey);
        long bufferSize = 1024 * 1024;
        bufferSize = Math.Min(bufferSize, input.CanSeek ? input.Length : bufferSize);
        var useMemCache = input.CanSeek ? input.Length <= _smallFileSizeLimit : false;
        var smallFileData = (useMemCache) ? new MemoryStream((int)input.Length) : null;
        IAppendStream? output = null;
        var persistToDisc = !fileKey.Adjustment.Temporary || !useMemCache;
        try {
            var buffer = new byte[bufferSize];
            if (persistToDisc) output = _io.OpenAppend(filePath);
            while (true) {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                if (output != null) 
                    await output.AppendAsyncNoChecksumOrLock(buffer, read);
                if (smallFileData != null) smallFileData.Write(buffer, 0, read);
            }
            if (smallFileData != null) _smallCache.Set(fileKey.GetKey(), smallFileData.ToArray(), (int)smallFileData.Length);
        } finally {
            if (output != null) output.Dispose();
            input.Dispose();
        }
    }
    public void Clear(FileIdWithAdjustment id) {
        var key = id.GetKey();
        _io.DeleteFileIfItExists(getFilePath(id));
        _io.DeleteFileIfItExists(getFilePathErrorStatus(key));
        _smallCache.Clear_EvenIf0Size(key);
    }
    public void ClearAll() {
        var folders = _io.GetFoldersAsync([_baseFolder], false, false).Result;
        foreach (var folder in folders) {
            if (!isShardFolder(folder.Name)) continue; // never the temp folder: conversions in progress are writing there
            _io.DeleteFolderIfItExists([_baseFolder, folder.Name]); // the sharded key folders live below the cache base folder
        }
        _smallCache.ClearAll_NotSize0();
    }
    public void SaveErrorStatus(FileIdWithAdjustment id, string errorMessage) {
        var key = id.GetKey();
        _io.DeleteFileIfItExists(getFilePath(id));
        _io.WriteString(getFilePathErrorStatus(key), errorMessage); // WriteString deletes any existing file first
    }
    /// <summary>Deletes every error status in the cache, leaving the converted files. An error is
    /// a file next to the conversion it belongs to, so this walks the whole cache: it is an
    /// explicit action, not something on a request path.</summary>
    public void ClearAllErrors() {
        var root = _io.GetFolderAsync([_baseFolder], true, true).Result;
        deleteErrorStatusFiles(root, [_baseFolder]);
    }
    void deleteErrorStatusFiles(FolderMeta folder, string[] path) {
        foreach (var file in folder.Files) {
            var name = file.KeyOf().FileName();
            if (!name.EndsWith(_errorStatusExtension, StringComparison.OrdinalIgnoreCase)) continue;
            _io.DeleteFileIfItExists([.. path, name]);
        }
        foreach (var sub in folder.SubFolders) {
            if (path.Length == 1 && !isShardFolder(sub.Name)) continue; // nothing of the cache is below the temp folder
            deleteErrorStatusFiles(sub, [.. path, sub.Name]);
        }
    }
}
