using Relatude.DB.Common;
namespace Relatude.DB.DataStores.Files;

public class MultiStorageFileMeta(Guid id, long length, byte[] checkSum, string name) {
    public Guid Id { get; set; } = id; // A unique identifier for the file in the storage
    public long Length { get; set; } = length;
    public byte[] Checksum { get; set; } = checkSum;
    public string Name { get; set; } = name;
    public static MultiStorageFileMeta FromFileValue(FileValue value) {
        var bytes = FileValue.GetFileKeyData(value);
        var id = new Guid(bytes);
        var checksum = Convert.FromHexString(value.Hash);
        return new MultiStorageFileMeta(id, value.Size, checksum, value.Name);
    }
    public FileValue ToFileValue(Guid storageId) {
        var hash = Convert.ToHexString(Checksum);
        var bytes = Id.ToByteArray();
        return FileValue.CreateNew(Name, Length, hash, storageId, bytes);
    }
}
