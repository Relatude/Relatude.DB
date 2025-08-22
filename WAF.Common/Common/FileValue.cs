namespace WAF.Common;
public enum FileValueType {
    Other,
    Document,
    Image,
    Video,
    Audio,
}
public class FileValue {
    public FileValue() {
        IsEmpty = true;
        Name = string.Empty;
        Indexed = false;
        TextExtract = string.Empty;
        MetaJSON = string.Empty;
        Type = FileValueType.Other;
        Size = 0;
        Height = 0;
        Width = 0;
        Hash = string.Empty;
        __StorageId = Guid.Empty;
        __StorageKey = [];
    }
    private FileValue(string name, long size, string hash, Guid storageId, byte[] storageKey) {
        IsEmpty = false;
        Name = name;
        TextExtract = string.Empty;
        MetaJSON = string.Empty;
        Type = FileValueType.Other;
        Size = size;
        Height = 0;
        Width = 0;
        Hash = hash;
        __StorageId = storageId;
        __StorageKey = storageKey;
    }
    public static FileValue Empty { get; } = new FileValue();
    public static FileValue CreateNew(string name, long size, string hash, Guid storageId, byte[] storageKey) {
        return new FileValue(name, size, hash, storageId, storageKey);
    }
    public static FileValue CreateMerge(FileValue old, FileValue newValue) {
        var f = new FileValue();

        // never set externally:
        if (old.IsEmpty) return f; // return empty, not allowed to change if old is empty
        f.Size = old.Size;
        f.__StorageId = old.__StorageId;
        f.__StorageKey = old.__StorageKey;

        // allow new values:
        f.IsEmpty = false;
        f.Name = newValue.Name;
        f.TextExtract = newValue.TextExtract;
        f.MetaJSON = newValue.MetaJSON;
        f.Type = newValue.Type;
        f.Height = newValue.Height;
        f.Width = newValue.Width;
        f.Hash = old.Hash;

        return f;
    }
    public bool IsEmpty { get; private set; }
    public bool Indexed { get; private set; }
    public string Hash { get; private set; }
    public long Size { get; private set; }

    public string Name { get; set; }
    public string TextExtract { get; set; }
    public string MetaJSON { get; set; }
    public FileValueType Type { get; set; }
    public int Height { get; set; }
    public int Width { get; set; }

    private Guid __StorageId { get; set; }
    private byte[] __StorageKey { get; set; }
    public static byte[] GetStorageKey(FileValue v) => v.__StorageKey;
    public static Guid GetStorageId(FileValue v) => v.__StorageId;

    private static int version = 0; // to allow for future changes
    public byte[] ToBytes() {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(IsEmpty);
        if (IsEmpty) return ms.ToArray();
        bw.Write(version);
        bw.Write(Name);
        bw.Write(TextExtract);
        bw.Write(MetaJSON);
        bw.Write((int)Type);
        bw.Write(Size);
        bw.Write(Height);
        bw.Write(Width);
        bw.Write(Hash);
        bw.Write(__StorageId.ToByteArray());
        bw.Write(__StorageKey.Length);
        bw.Write(__StorageKey);
        return ms.ToArray();
    }
    public static FileValue FromBytes(byte[] bytes) {
        var ms = new MemoryStream(bytes);
        var br = new BinaryReader(ms);
        var isEmpty = br.ReadBoolean();
        if (isEmpty) return Empty;
        var v = new FileValue();
        v.Name = br.ReadString();
        v.TextExtract = br.ReadString();
        v.MetaJSON = br.ReadString();
        v.Type = (FileValueType)br.ReadInt32();
        v.Size = br.ReadInt64();
        v.Height = br.ReadInt32();
        v.Width = br.ReadInt32();
        v.Hash = br.ReadString();
        v.__StorageId = new Guid(br.ReadBytes(16));
        var keyLength = br.ReadInt32();
        v.__StorageKey = br.ReadBytes(keyLength);
        return v;
    }
    public FileValue Copy() {
        var v = new FileValue();
        v.IsEmpty = IsEmpty;
        v.Name = Name;
        v.TextExtract = TextExtract;
        v.MetaJSON = MetaJSON;
        v.Type = Type;
        v.Size = Size;
        v.Height = Height;
        v.Width = Width;
        v.Hash = Hash;
        return v;
    }

    public static bool AreValuesEqual(FileValue b1, FileValue b2) {
        if (b1.Width != b2.Width) return false;
        if (b1.Height != b2.Height) return false;
        if (b1.Hash != b2.Hash) return false;
        if (b1.TextExtract != b2.TextExtract) return false;
        if (b1.MetaJSON != b2.MetaJSON) return false;
        if (b1.Indexed != b2.Indexed) return false;
        if (b1.Name != b2.Name) return false;
        if (b1.Size != b2.Size) return false;
        return true;

    }
}

