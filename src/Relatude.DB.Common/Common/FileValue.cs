namespace Relatude.DB.Common;
public class FileValue {
    public FileValue() {
        IsEmpty = true;
        Name = string.Empty;
        Indexed = false;
        TextExtract = string.Empty;
        MetaJSON = string.Empty;
        Type = FileBaseFormats.Unknown;
        Size = 0;
        Height = 0;
        Width = 0;
        Hash = string.Empty;
        _storageId = Guid.Empty;
        _fileKeyData = [];
    }
    private FileValue(string name, long size, string hash, Guid storageId, byte[] storageKey) {
        IsEmpty = false;
        Name = name;
        TextExtract = string.Empty;
        MetaJSON = string.Empty;
        Type = FileBaseFormats.Unknown;
        Size = size;
        Height = 0;
        Width = 0;
        Hash = hash;
        _storageId = storageId;
        _fileKeyData = storageKey;
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
        f._storageId = old._storageId;
        f._fileKeyData = old._fileKeyData;

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
    public string Hash { get; private set; } // A hash of the file content, used for integrity check and a works as a file key
    public long Size { get; private set; }

    public string Name { get; set; }
    public string TextExtract { get; set; }
    public string MetaJSON { get; set; }
    public FileBaseFormats Type { get; set; }
    public int Height { get; set; }
    public int Width { get; set; }

    private Guid _storageId { get; set; }
    private byte[] _fileKeyData { get; set; }
    public static byte[] GetFileKeyData(FileValue v) => v._fileKeyData; // A data that to identify the file in the storage provider. 
    public static Guid GetStorageId(FileValue v) => v._storageId;  // Id is used to identify the storage provider

    private static int version = 0; // to allow for future changes
    public byte[] ToBytes() {
        if (IsEmpty) return [];
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
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
        bw.Write(_storageId.ToByteArray());
        bw.Write(_fileKeyData.Length);
        bw.Write(_fileKeyData);
        return ms.ToArray();
    }
    public static FileValue FromBytes(byte[] bytes) {
        if(bytes.Length == 0) return Empty;
        var ms = new MemoryStream(bytes);
        var br = new BinaryReader(ms);
        var version = br.ReadInt32();
        if(version != 0) throw new Exception($"Unsupported FileValue version: {version}");
        var v = new FileValue();
        v.Name = br.ReadString();
        v.TextExtract = br.ReadString();
        v.MetaJSON = br.ReadString();
        v.Type = (FileBaseFormats)br.ReadInt32();
        v.Size = br.ReadInt64();
        v.Height = br.ReadInt32();
        v.Width = br.ReadInt32();
        v.Hash = br.ReadString();
        v._storageId = new Guid(br.ReadBytes(16));
        var keyLength = br.ReadInt32();
        v._fileKeyData = br.ReadBytes(keyLength);
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

