namespace WAF.Common;
public class CultureString {
    string? _primary;
    Dictionary<int, string>? _dic;
    public CultureString() { }
    public CultureString(string? primary) {
        _primary = primary;
    }
    public void Set(string value) {
        _primary = value;
    }
    public void Set(int lcid, string value) {
        if (_dic == null) _dic = new();
        _dic[lcid] = value;
        if (_primary == null) _primary = value;
    }
    public string Get() {
        return _primary ?? string.Empty;
    }
    public string Get(int lcid) {
        if (_dic != null && _dic.TryGetValue(lcid, out var value)) return value;
        return _primary ?? string.Empty;
    }
    public override string ToString() => _primary ?? string.Empty;
    static public CultureString FromBytes(byte[] bytes) {
        using var ms = new MemoryStream(bytes);
        return FromStream(ms);
    }
    static public CultureString FromStream(Stream s) {
        var cs = new CultureString();
        cs._primary = s.ReadStringOrNull();
        var count = s.ReadInt();
        if (count > 0) {
            cs._dic = new();
            for (var i = 0; i < count; i++) {
                var lcid = s.ReadInt();
                var value = s.ReadString();
                cs._dic[lcid] = value;
            }
        }
        return cs;
    }
    public void AppendStream(Stream s) {
        s.WriteStringOrNull(_primary);
        if (_dic == null) {
            s.WriteInt(0);
        } else {
            s.WriteInt(_dic.Count);
            foreach (var kv in _dic) {
                s.WriteInt(kv.Key);
                s.WriteString(kv.Value);
            }
        }
    }
}
