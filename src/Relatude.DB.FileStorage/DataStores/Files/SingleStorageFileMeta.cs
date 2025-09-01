using Relatude.DB.Common;
namespace Relatude.DB.DataStores.Files;
public class SingleStorageFileMeta(Guid id, long offset, long length, byte[] checkSum, string name) {
    public Guid Id { get; set; } = id;
    public long Offset { get; set; } = offset;
    public long Length { get; set; } = length;
    public byte[] Checksum { get; set; } = checkSum;
    public string Name { get; set; } = name;
    public static SingleStorageFileMeta FromFileValue(FileValue value) {
        var bytes = FileValue.GetStorageKey(value);
        var offset = BitConverter.ToInt64(bytes, 0);
        var id = new Guid(bytes.Skip(8).ToArray());
        var checksum = Convert.FromHexString(value.Hash);
        return new SingleStorageFileMeta(id, offset, value.Size, checksum, value.Name);
    }
    public FileValue ToFileValue(Guid storageId) {
        var hash = Convert.ToHexString(Checksum);
        var bytes = new byte[8 + 16];
        BitConverter.GetBytes(Offset).CopyTo(bytes, 0);
        Id.ToByteArray().CopyTo(bytes, 8);
        return FileValue.CreateNew(Name, Length, hash, storageId, bytes);
    }
}
