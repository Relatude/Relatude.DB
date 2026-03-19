using Relatude.DB.Common;
namespace Relatude.DB.DataStores.Files;

public class MultiStorageFileMeta(Guid id, long length, byte[] checkSum, string name, string originalFileName) {
    public Guid Id { get; set; } = id; // A unique identifier for the file in the storage
    public long Length { get; set; } = length;
    public byte[] Checksum { get; set; } = checkSum;
    public string Name { get; set; } = name;
    public string OriginalFileName { get; } = originalFileName;
    public static MultiStorageFileMeta FromFileValue(FileValue value) {
        var bytes = FileValue.GetFileKeyData(value);
        var id = new Guid(bytes.Take(16).ToArray());
        var fileSystemFileName = System.Text.Encoding.UTF8.GetString(bytes.Skip(16).ToArray());
        var checksum = Convert.FromHexString(value.Hash);
        return new MultiStorageFileMeta(id, value.Size, checksum, value.Name, fileSystemFileName);
    }
    public FileValue ToFileValue(Guid storageId) {
        var hash = Convert.ToHexString(Checksum);
        var fileNameBytes = System.Text.Encoding.UTF8.GetBytes(OriginalFileName);
        var bytes = Id.ToByteArray().Concat(fileNameBytes).ToArray();
        return FileValue.CreateNew(Name, Length, hash, storageId, bytes);
    }
}
