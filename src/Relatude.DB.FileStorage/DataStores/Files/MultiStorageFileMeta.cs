using Relatude.DB.Common;
namespace Relatude.DB.DataStores.Files;

public class MultiStorageFileMeta(string name, long size, string hash, string originalFileName, string[] relPath) {
    public string Name { get; } = name;
    public long Size { get; set; } = size;
    public string Hash { get; set; } = hash;
    public string OriginalFileName { get; } = originalFileName;
    public string[] RelPath { get; } = relPath;
    public static MultiStorageFileMeta FromFileValue(FileValue value) {
        var bytes = FileValue.GetFileKeyData(value);
        var ms = new MemoryStream(bytes);
        var reader = new BinaryReader(ms);
        var originalFileName = reader.ReadString();
        var relPathLength = reader.ReadInt32();
        var relPath = new string[relPathLength];
        for (int i = 0; i < relPathLength; i++) relPath[i] = reader.ReadString();
        return new MultiStorageFileMeta(value.Name, value.Size, value.Hash, originalFileName, relPath);
    }
    public FileValue ToFileValue(Guid storageId) {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(OriginalFileName);
        bw.Write(RelPath.Length);
        foreach (var p in RelPath) bw.Write(p);
        return FileValue.CreateNew(Name, Size, Hash, storageId, ms.ToArray());
    }
}
