namespace Relatude.DB.Common;

public class FileValue {
    FileValue() {
        IsEmpty = true;
        Name = string.Empty;
        Indexed = false;
        TextExtract = string.Empty;
        MetaJSON = string.Empty;
        Type = FileType.Unknown;
        Size = 0;
        Height = 0;
        Width = 0;
        Hash = string.Empty;
        StorageId = Guid.Empty;
        FileId = Guid.Empty;
        _fileKeyData = [];
        PropertyPath = null;
    }
    FileValue(string name, long size, string hash, Guid storageId, Guid fileId, byte[] storageKey, PropertyPath propertyPath) {
        IsEmpty = false;
        Name = name;
        TextExtract = string.Empty;
        MetaJSON = string.Empty;
        Type = FileType.Unknown;
        Size = size;
        Height = 0;
        Width = 0;
        Hash = hash;
        StorageId = storageId;
        FileId = fileId;
        _fileKeyData = storageKey;
        PropertyPath = propertyPath;
    }
    public static FileValue CreateNew(string name, long size, string hash, Guid storageId, Guid fileId, byte[] storageKey, PropertyPath propertyPath) {
        return new FileValue(name, size, hash, storageId, fileId, storageKey, propertyPath);
    }
    public static FileValue CreateEmptyWithPropertyPath(PropertyPath propertyPath) {
        return new FileValue() { PropertyPath = propertyPath };
    }
    static FileValue CreateEmptyWithNoPropertyPath() {
        return new FileValue();
    }
    public static FileValue Empty { get; } = CreateEmptyWithNoPropertyPath();
    public static FileValue CopyAndEnsurePropertyPath(FileValue? value, PropertyPath propertyPath) {
        if (value == null || value.IsEmpty) {
            return CreateEmptyWithPropertyPath(propertyPath);
        } else {
            return CreateNew(value.Name, value.Size, value.Hash, value.StorageId, value.FileId, value._fileKeyData, propertyPath);
        }
    }
    public static FileValue BringChangesFromOuterToInner(FileValue old, FileValue newValue) {
        // allowing only certain props to be changed:
        var f = new FileValue();

        // never set externally:
        f.Size = old.Size;
        f.StorageId = old.StorageId;
        f.FileId = old.FileId;
        f._fileKeyData = old._fileKeyData;
        f.PropertyPath = old.PropertyPath;
        f.Hash = old.Hash;

        // allow new values:
        f.IsEmpty = f.IsEmpty;
        f.Name = newValue.Name;
        f.TextExtract = newValue.TextExtract;
        f.MetaJSON = newValue.MetaJSON;
        f.Type = newValue.Type;
        f.Height = newValue.Height;
        f.Width = newValue.Width;

        return f;
    }
    public bool IsEmpty { get; private set; }
    public PropertyPath? PropertyPath { get; private set; }
    public bool Indexed { get; private set; }
    public string Hash { get; private set; } // A hash of the file content, used for integrity check and a works as a file key
    public long Size { get; private set; }

    public string Name { get; set; }
    public string TextExtract { get; set; }
    public string MetaJSON { get; set; }
    public FileType Type { get; set; }
    public FileFormat Format {
        get {
            return FileFormatUtil.GetDetailedFormatFromFileName(Name);
        }
    }
    public string ContentType{
        get {
            return FileFormatUtil.GetContentType(Format);
        }
    }
    public int Height { get; set; }
    public int Width { get; set; }

    public Guid FileId { get; private set; }
    public Guid StorageId { get; private set; }
    private byte[] _fileKeyData { get; set; }

    public static byte[] GetFileKeyData(FileValue v) => v._fileKeyData; // A data that to identify the file in the storage provider. 

    private static int version = 0; // to allow for future changes
    public byte[] ToBytes() {
        if (IsEmpty) return [];
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(version);
        bw.Write(Name);
        bw.Write(TextExtract);
        bw.Write(MetaJSON);
        bw.Write((int)Type);
        bw.Write(Size);
        bw.Write(Height);
        bw.Write(Width);
        bw.Write(Hash);
        bw.Write(StorageId.ToByteArray());
        bw.Write(FileId.ToByteArray());
        bw.Write(_fileKeyData.Length);
        bw.Write(_fileKeyData);
        if (PropertyPath == null) bw.Write(new byte[0]);
        else bw.Write(PropertyPath.ToBytes());
        return ms.ToArray();
    }
    public static FileValue FromBytes(byte[] bytes, PropertyPath? propertyPath) {
        if (bytes.Length == 0) return Empty;
        var ms = new MemoryStream(bytes);
        var br = new BinaryReader(ms);
        var version = br.ReadInt32();
        if (version != 0) throw new Exception($"Unsupported FileValue version: {version}");
        var v = new FileValue();
        v.IsEmpty = false;
        v.Name = br.ReadString();
        v.TextExtract = br.ReadString();
        v.MetaJSON = br.ReadString();
        v.Type = (FileType)br.ReadInt32();
        v.Size = br.ReadInt64();
        v.Height = br.ReadInt32();
        v.Width = br.ReadInt32();
        v.Hash = br.ReadString();
        v.StorageId = new Guid(br.ReadBytes(16));
        v.FileId = new Guid(br.ReadBytes(16));
        var keyLength = br.ReadInt32();
        v._fileKeyData = br.ReadBytes(keyLength);
        if (ms.Position == ms.Length) {
            v.PropertyPath = null;
            return v;
        }
        var propertyPathBytes = br.ReadBytes((int)(ms.Length - ms.Position));
        v.PropertyPath = PropertyPath.FromBytes(propertyPathBytes);
        return v;
    }
    public FileValue Copy() {
        var c = new FileValue();
        c.IsEmpty = IsEmpty;
        c.Name = Name;
        c.TextExtract = TextExtract;
        c.MetaJSON = MetaJSON;
        c.Type = Type;
        c.Size = Size;
        c.Height = Height;
        c.Width = Width;
        c.Hash = Hash;
        c.StorageId = StorageId;
        c.FileId = FileId;
        c._fileKeyData = _fileKeyData;
        c.PropertyPath = PropertyPath;
        return c;
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
        if (b1.StorageId != b2.StorageId) return false;
        if (b1.FileId != b2.FileId) return false;
        if (b1.PropertyPath != b2.PropertyPath) return false;
        return true;
    }
}

