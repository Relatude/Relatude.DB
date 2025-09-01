using Relatude.DB.Common;
namespace Relatude.DB.DataStores;
public class StoreCollection {
    public StoreCollection(Guid id, string? name = null, Guid[]? cultures = null) {
        Id = id;
        Name = new(name);
        Cultures = cultures ?? Array.Empty<Guid>();
    }
    public Guid Id { get; }
    public CultureString Name { get; }
    public Guid[] Cultures { get; }
    public override string ToString() => Name.Get();
    static public StoreCollection FromStream(Stream s) {
        var id = s.ReadGuid();
        var name = CultureString.FromStream(s);
        var count = s.ReadInt();
        var cultures = new Guid[count];
        for (var i = 0; i < count; i++) {
            cultures[i] = s.ReadGuid();
        }
        return new StoreCollection(id, name.Get(), cultures);
    }
    public void AppendStream(Stream s) {
        s.WriteGuid(Id);
        Name.AppendStream(s);
        s.WriteInt(Cultures.Length);
        foreach (var c in Cultures) {
            s.WriteGuid(c);
        }
    }
    public static StoreCollection CreateNewWithDefaults(Guid cultureId) {
        return new StoreCollection(Guid.NewGuid(), "Default", new[] { cultureId });
    }
}
