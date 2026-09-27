using System.Diagnostics.CodeAnalysis;
using Relatude.DB.IO;

namespace Relatude.Logger;
/// <summary>
/// Delegates to an inner provider, but can hold up the reading of one log's entries: while the gate
/// is closed, a read of a file whose name starts with the given prefix waits for it to open. A log
/// reads its files holding its lock, so a closed gate is to anyone recording at the time what a page
/// or a search of a large log is - for exactly as long as the test wants it to be.
/// </summary>
sealed class GatedIOProvider(IIOProvider inner, string fileNamePrefix) : IIOProvider {
    readonly ManualResetEventSlim _open = new(true);
    /// <summary>Set once a read has come to the closed gate and is waiting there.</summary>
    public ManualResetEventSlim ReadWaiting { get; } = new(false);
    public void Close() {
        ReadWaiting.Reset();
        _open.Reset();
    }
    public void Open() => _open.Set();
    public IReadStream OpenRead(string[] path, long position) {
        if (!_open.IsSet && path.FileName().StartsWith(fileNamePrefix, StringComparison.OrdinalIgnoreCase)) {
            ReadWaiting.Set();
            // a test failing with the gate closed must not leave the reader waiting for ever
            if (!_open.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("The gate was never opened.");
        }
        return inner.OpenRead(path, position);
    }
    public IAppendStream OpenAppend(string[] path) => inner.OpenAppend(path);
    public bool Exists(string[] path) => inner.Exists(path);
    public bool DoesNotExistOrIsEmpty(string[] path) => inner.DoesNotExistOrIsEmpty(path);
    public void DeleteFileIfItExists(string[] path) => inner.DeleteFileIfItExists(path);
    public FileMeta[] GetFiles() => inner.GetFiles();
    public long GetFileSizeOrZeroIfUnknown(string[] path) => inner.GetFileSizeOrZeroIfUnknown(path);
    public bool CanRenameFile => inner.CanRenameFile;
    public void RenameFile(string[] path, string[] newPath) => inner.RenameFile(path, newPath);
    public bool CanRenameFolder => inner.CanRenameFolder;
    public void RenameFolder(string[] path, string[] newPath) => inner.RenameFolder(path, newPath);
    public bool SupportsEmptyFolders => inner.SupportsEmptyFolders;
    public bool CanTruncate => inner.CanTruncate;
    public void TruncateFile(string[] path, long newLength) => inner.TruncateFile(path, newLength);
    public void CloseAllOpenStreams() => inner.CloseAllOpenStreams();
    public bool TryGetLocalFilePath(string[] path, [MaybeNullWhen(false)] out string localFilePath) => inner.TryGetLocalFilePath(path, out localFilePath);
    public bool TryGetLocalFolderPath(string[] path, [MaybeNullWhen(false)] out string localFolderPath) => inner.TryGetLocalFolderPath(path, out localFolderPath);
    public bool TryMoveIfSameDrive(string fromLocalFilePath, string[] destination) => inner.TryMoveIfSameDrive(fromLocalFilePath, destination);
    public void DeleteFolderIfItExists(string[] path) => inner.DeleteFolderIfItExists(path);
    public void EnsureFolder(string[] path) => inner.EnsureFolder(path);
    public Task<FolderMeta> GetFolderAsync(string[] path, bool recursive, bool withFiles) => inner.GetFolderAsync(path, recursive, withFiles);
}
