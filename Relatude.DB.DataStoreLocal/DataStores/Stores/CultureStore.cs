using Relatude.DB.IO;
using Relatude.DB.Transactions;
namespace Relatude.DB.DataStores.Stores;
internal class CultureStore : IDisposable {
    readonly Dictionary<Guid, StoreCulture> _cuturesById;
    readonly Dictionary<int, StoreCulture> _cuturesByLCID;
    public CultureStore() {
        _cuturesById = new();
        _cuturesByLCID = new();
    }
    public void Add(StoreCulture culture) {
        _cuturesById.Add(culture.Id, culture);
        _cuturesByLCID.Add(culture.LCID, culture);
    }
    public void Remove(Guid cultureId) {
        _cuturesByLCID.Remove(_cuturesById[cultureId].LCID);
        _cuturesById.Remove(cultureId);
    }
    public void Update(StoreCulture culture) {
        _cuturesById[culture.Id] = culture;
        _cuturesByLCID[culture.LCID] = culture;
    }
    public bool Contains(Guid cultureId) => _cuturesById.ContainsKey(cultureId);
    public bool Contains(int lcid) => _cuturesByLCID.ContainsKey(lcid);
    static Guid _startMarker = new Guid("fc6767fc-903e-4ddb-a75a-92dc4fd1167f");
    static Guid _endMarker = new Guid("3cf56739-4c6b-4a96-aedf-5b9f36ee848d");
    public void SaveState(IAppendStream stream) {
        stream.WriteGuid(_startMarker);
        stream.WriteVerifiedInt(_cuturesById.Count);
        var ms = new MemoryStream();
        foreach (var culture in _cuturesById.Values) culture.AppendStream(ms);
        stream.WriteByteArray(ms.ToArray());
        stream.WriteGuid(_endMarker);
    }
    public void ReadState(IReadStream stream) {
        stream.ValidateMarker(_startMarker);
        var noCults = stream.ReadVerifiedInt();
        var ms = new MemoryStream(stream.ReadByteArray());
        stream.ValidateMarker(_endMarker);
        for (int i = 0; i < noCults; i++) Add(StoreCulture.FromStream(ms));
    }
    public void Dispose() {
    }
}
