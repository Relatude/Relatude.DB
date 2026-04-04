using Relatude.DB.Common;

namespace Relatude.DB.DataStores.Files;

public static class IFileStoreExtenstions {
    public static async Task<FileValue> Insert(this IFileStore store, string path, string? fileName = null) {
        using var stream = new FileStream(path, FileMode.Open);
        if (fileName == null) fileName = Path.GetFileName(path);
        var newFileId = Guid.NewGuid();
        var r = await store.InsertAsync(newFileId, stream, fileName);
        return FileValue.CreateNew(fileName, r.Length, r.FileHash, store.Id, newFileId, r.StoreKey);
    }
    public static async Task Extract(this IFileStore store, FileValue meta, string path) {
        using var stream = new FileStream(path, FileMode.CreateNew);
        await store.ExtractAsync(meta, stream);
    }
}
