using System.Globalization;
using Relatude.DB.Common;
namespace Relatude.DB.DataStores;
public class StoreCulture {
    const int FALLBACK_LCID = 1033;
    public StoreCulture(Guid id, int lcid, string name) {
        Id = id;
        LCID = lcid;
        Name = new();
        Name.Set(lcid, name);
    }
    public Guid Id { get; }
    public int LCID { get; }
    public CultureString Name { get; }
    public override string ToString() => Name.Get();
    static public StoreCulture FromStream(Stream s) {
        var id = s.ReadGuid();
        var lcid = s.ReadInt();
        var name = CultureString.FromStream(s);
        return new StoreCulture(id, lcid, name.Get());
    }
    public void AppendStream(Stream s) {
        s.WriteGuid(Id);
        s.WriteInt(LCID);
        Name.AppendStream(s);
    }
    public static StoreCulture CreateNewWithDefaults(int lcid = FALLBACK_LCID) {
        return new StoreCulture(Guid.NewGuid(), lcid, CultureInfo.GetCultureInfo(lcid).Name);
    }
    public string FilePrefix() {
        return Id.ToString().Replace("-", "").ToLower() + "_";
    }
}

