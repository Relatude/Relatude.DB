using Relatude.DB.Common;
using Relatude.DB.IO;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.FileConversion;

internal class FileConversionCache {
    readonly IIOProvider _io;
    readonly string _baseFolder;
    readonly TimeSpan _errorRetryAfter;
    /// <param name="errorRetryAfter">How long a failed conversion is reported as failed before the next
    /// request tries it again. <see cref="DefaultErrorRetryAfter"/> when not given.</param>
    public FileConversionCache(IIOProvider io, string baseFolder, TimeSpan? errorRetryAfter = null) {
        _io = io;
        _baseFolder = baseFolder;
        _errorRetryAfter = errorRetryAfter ?? DefaultErrorRetryAfter;
    }
    /// <summary>A failure can be passing - the source briefly unreachable, a converter still being
    /// installed - so a saved error is only trusted this long. A conversion canceled for good is kept
    /// until the errors are cleared.</summary>
    public static readonly TimeSpan DefaultErrorRetryAfter = TimeSpan.FromHours(1);
    /// <summary>The folder below the cache base folder that conversions in progress write their
    /// output to. It is not part of the cache: stale files in it are removed when the store opens and
    /// it is left alone when the cache is cleared, as a running conversion may be writing in it.</summary>
    public const string TempFolderName = "temp";
    /// <summary>The file below the cache base folder holding <see cref="Generation"/>.</summary>
    const string _generationFileName = "generation";
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
    /// <summary>Where one write is assembled before it is moved onto its key, so a cached file is
    /// never seen half written - not by a request, and not after a crash in the middle of the write.</summary>
    string[] newTempPath() => [_baseFolder, TempFolderName, Guid.NewGuid().ToString("N") + ".part"];
    static string extensionOf(FileFormat format) => FileFormatUtil.GetExtensionWithDot(format) ?? _unknownFormatExtension;
    /// <summary>Whether the folder is one of the two levels the keys are sharded over, named by
    /// the leading hex chars of the key. Everything else below the base folder is not cache
    /// content: <see cref="TempFolderName"/> is the one such folder today.</summary>
    static bool isShardFolder(string name) => name.Length == 2 && name.All(char.IsAsciiHexDigit);
    /// <summary>An empty file is not a conversion: it is what a write that never completed left
    /// behind, before writes were moved into place whole. It is converted again and replaced.</summary>
    bool hasFile(string[] path) => !_io.DoesNotExistOrIsEmpty(path);

    /// <summary>
    /// A random id kept in the cache folder and replaced when the cache is cleared. File URLs carry a
    /// version made from it and the file's content hash, so a URL stays the same across restarts -
    /// browsers and CDNs keep their copies - and changes when what it serves may have: another file, or
    /// a cleared cache converting again. Read on first use; another process sharing the folder sees a
    /// replacement when it restarts.
    /// </summary>
    public Guid Generation {
        get {
            lock (_generationLock) return _generation ??= readOrCreateGeneration();
        }
    }
    Guid? _generation;
    readonly object _generationLock = new();
    string[] generationPath => [_baseFolder, _generationFileName];
    Guid readOrCreateGeneration() {
        try {
            if (Guid.TryParse(_io.ReadString(generationPath), out var existing) && existing != Guid.Empty) return existing;
        } catch { } // unreadable: replaced below
        return writeNewGeneration();
    }
    Guid writeNewGeneration() {
        var generation = Guid.NewGuid();
        try {
            _io.WriteString(generationPath, generation.ToString());
        } catch { } // a provider that cannot be written to: the id holds for this process only
        return generation;
    }

    public bool TryGetStatusNoStream(FileIdWithAdjustment id, [MaybeNullWhen(false)] out FileConversionProgressInfo progress) {
        var key = id.GetKey();
        if (_smallCache.TryGet(key, out _)) {
            progress = new(FileConversionStatus.Ready, 100, 0, null);
            return true;
        }
        if (hasFile(getFilePath(id))) {
            progress = new(FileConversionStatus.Ready, 100, 0, null);
            return true;
        }
        if (tryReadError(key, out var errorMessage)) {
            progress = new(FileConversionStatus.Error, 0, 0, errorMessage);
            return true;
        }
        progress = null;
        return false;
    }

    public bool TryGetResultAndStream(FileIdWithAdjustment id, [MaybeNullWhen(false)] out FileConversionResultAndStream result) {
        var key = id.GetKey();
        if (_smallCache.TryGet(key, out var smallData)) {
            result = new(new(FileConversionStatus.Ready, 100, 0, null), readOnlyStream(smallData));
            return true;
        }
        var path = getFilePath(id);
        if (hasFile(path)) {
            var stream = _io.OpenRead(path, 0).AsStream();
            var length = stream.Length;
            if (length <= _smallFileSizeLimit) {
                var buffer = new byte[length];
                stream.ReadExactly(buffer, 0, (int)length);
                stream.Dispose();
                _smallCache.Set(key, buffer, (int)length);
                result = new(new(FileConversionStatus.Ready, 100, 0, null), readOnlyStream(buffer));
            } else {
                result = new(new(FileConversionStatus.Ready, 100, 0, null), stream);
            }
            return true;
        }
        if (tryReadError(key, out var errorMessage)) {
            result = new(new(FileConversionStatus.Error, 0, 0, errorMessage), null);
            return true;
        }
        result = null;
        return false;
    }
    // the array is the one in the memory cache, shared by every request for the file
    static MemoryStream readOnlyStream(byte[] cached) => new(cached, writable: false);

    public async Task SetFromFileAsync(FileIdWithAdjustment fileKey, string localFilePath) {
        var filePath = getFilePath(fileKey);
        if (hasFile(filePath)) { // converted already, by an earlier run or another process sharing the cache: that copy stays
            tryDeleteLocalFile(localFilePath);
            return;
        }
        _io.DeleteFileIfItExists(filePath); // an empty leftover
        if (_io.TryMoveIfSameDrive(localFilePath, filePath)) { // a rename, so the file appears whole; fails if another writer got there first
            completed(fileKey);
            return;
        }
        await SetFromStreamAsync(fileKey, File.OpenRead(localFilePath));
        tryDeleteLocalFile(localFilePath);
    }
    static void tryDeleteLocalFile(string localFilePath) {
        try {
            File.Delete(localFilePath);
        } catch { } // left to the temp folder clean up
    }
    public async Task SetFromStreamAsync(FileIdWithAdjustment fileKey, Stream input) {
        var filePath = getFilePath(fileKey);
        long bufferSize = 1024 * 1024;
        bufferSize = Math.Min(bufferSize, input.CanSeek ? input.Length : bufferSize);
        var useMemCache = input.CanSeek ? input.Length <= _smallFileSizeLimit : false;
        var smallFileData = (useMemCache) ? new MemoryStream((int)input.Length) : null;
        IAppendStream? output = null;
        string[]? writePath = null;
        var moveWhenWritten = false;
        var persistToDisc = !fileKey.Adjustment.Temporary || !useMemCache;
        try {
            if (persistToDisc) {
                if (hasFile(filePath)) return; // converted already, by an earlier run or another process sharing the cache: that copy stays
                if (_io.CanRenameFile) {
                    writePath = newTempPath();
                    moveWhenWritten = true;
                } else {
                    // no rename to move a finished file into place (blob storage): written on its key, after
                    // whatever an unfinished write left there, so nothing is ever appended to an old copy
                    _io.DeleteFileIfItExists(filePath);
                    writePath = filePath;
                }
                output = _io.OpenAppend(writePath);
            }
            var buffer = new byte[bufferSize];
            while (true) {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                if (output != null)
                    await output.AppendAsyncNoChecksumOrLock(buffer, read);
                if (smallFileData != null) smallFileData.Write(buffer, 0, read);
            }
            if (output != null) {
                output.Dispose();
                output = null;
                if (moveWhenWritten) moveIntoPlace(writePath!, filePath);
                completed(fileKey);
            }
            if (smallFileData != null) _smallCache.Set(fileKey.GetKey(), smallFileData.ToArray(), (int)smallFileData.Length);
        } catch {
            if (output != null) {
                output.Dispose();
                output = null;
            }
            if (writePath != null) {
                try { _io.DeleteFileIfItExists(writePath); } catch { } // never leave a partial file behind, on its key or in the temp folder
            }
            throw;
        } finally {
            if (output != null) output.Dispose();
            input.Dispose();
        }
    }
    // Moves a completely written file onto its key. Another writer may have got there first (a second
    // run of the same conversion, another process sharing the cache): its copy is as good, so it stays.
    void moveIntoPlace(string[] tempPath, string[] filePath) {
        try {
            if (hasFile(filePath)) {
                _io.DeleteFileIfItExists(tempPath);
                return;
            }
            _io.DeleteFileIfItExists(filePath); // an empty leftover
            _io.RenameFile(tempPath, filePath);
        } catch when (hasFile(filePath)) {
            _io.DeleteFileIfItExists(tempPath); // lost the race to another writer
        }
    }
    // a conversion that has its file has no error any more: one that failed before and was tried again
    void completed(FileIdWithAdjustment id) => _io.DeleteFileIfItExists(getFilePathErrorStatus(id.GetKey()));
    public void Clear(FileIdWithAdjustment id) {
        var key = id.GetKey();
        _io.DeleteFileIfItExists(getFilePath(id));
        _io.DeleteFileIfItExists(getFilePathErrorStatus(key));
        _smallCache.Clear_EvenIf0Size(key);
    }
    /// <summary>Deletes every converted file and error, and replaces <see cref="Generation"/> so the
    /// URLs handed out from now on differ from those browsers may have cached the old files under.</summary>
    public void ClearAll() {
        var folders = _io.GetFoldersAsync([_baseFolder], false, false).Result;
        foreach (var folder in folders) {
            if (!isShardFolder(folder.Name)) continue; // never the temp folder: conversions in progress are writing there
            _io.DeleteFolderIfItExists([_baseFolder, folder.Name]); // the sharded key folders live below the cache base folder
        }
        _smallCache.ClearAll_NotSize0();
        lock (_generationLock) _generation = writeNewGeneration();
    }
    /// <param name="permanent">A conversion canceled for good: reported until the errors are cleared,
    /// rather than tried again after <see cref="DefaultErrorRetryAfter"/>.</param>
    public void SaveErrorStatus(FileIdWithAdjustment id, string errorMessage, bool permanent = false) {
        var key = id.GetKey();
        _io.DeleteFileIfItExists(getFilePath(id));
        _smallCache.Clear_EvenIf0Size(key); // the error replaces the result in memory as well as on disk
        _io.WriteString(getFilePathErrorStatus(key), formatError(DateTime.UtcNow, permanent, errorMessage)); // WriteString deletes any existing file first
    }
    // An error is saved as "status2|{utc ticks}|{p or t}|{message}". One saved before the time was
    // recorded is the bare message: it is tried again at once, unless it was a permanent cancellation.
    const string _errorFormatPrefix = "status2|";
    static string formatError(DateTime savedUtc, bool permanent, string message)
        => _errorFormatPrefix + savedUtc.Ticks + "|" + (permanent ? "p" : "t") + "|" + message;
    bool tryReadError(Guid key, [MaybeNullWhen(false)] out string message) {
        var path = getFilePathErrorStatus(key);
        message = null;
        if (!_io.Exists(path)) return false;
        var content = _io.ReadString(path, "Error");
        DateTime savedUtc;
        bool permanent;
        var parts = content.Split('|', 4);
        if (content.StartsWith(_errorFormatPrefix, StringComparison.Ordinal) && parts.Length == 4 && long.TryParse(parts[1], out var ticks) && ticks >= 0 && ticks <= DateTime.MaxValue.Ticks) {
            savedUtc = new DateTime(ticks, DateTimeKind.Utc);
            permanent = parts[2] == "p";
            content = parts[3];
        } else {
            savedUtc = DateTime.MinValue;
            permanent = content.Contains("permanently", StringComparison.OrdinalIgnoreCase);
        }
        if (!permanent && DateTime.UtcNow - savedUtc >= _errorRetryAfter) return false; // expired: the next request converts again, and its outcome replaces this
        message = content;
        return true;
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
    /// <summary>
    /// Deletes what is left in the temp folder by writes that never completed: files not written to
    /// for <paramref name="maxAge"/>. Younger files stay, as a conversion may still be writing them - in
    /// this process after a reopen of the store, or in another one sharing the cache. For providers
    /// without a local folder; a local temp folder is cleaned by the engine, which also has the files
    /// the converters write there themselves.
    /// </summary>
    public (int Deleted, int Kept) DeleteStaleTempFiles(TimeSpan maxAge) {
        var cutoff = DateTime.UtcNow - maxAge;
        int deleted = 0, kept = 0;
        var folder = _io.GetFolderAsync([_baseFolder, TempFolderName], false, true).Result;
        foreach (var file in folder.Files) {
            if (file.LastModifiedUtc > cutoff) {
                kept++;
                continue;
            }
            try {
                _io.DeleteFileIfItExists(file.KeyOf());
                deleted++;
            } catch {
                kept++;
            }
        }
        return (deleted, kept);
    }
}
