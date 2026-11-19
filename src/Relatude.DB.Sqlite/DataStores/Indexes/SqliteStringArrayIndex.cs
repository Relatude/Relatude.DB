using System.Text.Json;
using Relatude.DB.DataStores.Sets;
namespace Relatude.DB.DataStores.Indexes;

public class SqliteStringArrayIndex : PersistedStringArrayIndexBase {
    readonly SqliteIndexStore _store;
    readonly string _tableName;
    public SqliteStringArrayIndex(SetRegister sets, SqliteIndexStore store, string indexId, string tableName, string friendlyName, bool justCreated)
        : base(store, justCreated, sets, indexId, friendlyName) {
        _store = store;
        _tableName = tableName;
    }
    // one row per node; the array is packed into a single JSON TEXT value and queries run on the
    // in-memory mirror, so the table needs no value index
    protected override IEnumerable<KeyValuePair<int, string[]>> ReadAllPersisted()
        => _store.Rows("SELECT id, value FROM " + _tableName, r => new KeyValuePair<int, string[]>(r.GetInt32(0), JsonSerializer.Deserialize<string[]>(r.GetString(1))!));
    protected override void PersistAdd(int nodeId, string[] value)
        => _store.Execute("INSERT INTO " + _tableName + " (id, value) VALUES (@id, @value)", ("@id", nodeId), ("@value", JsonSerializer.Serialize(value)));
    protected override void PersistRemove(int nodeId) => _store.Execute("DELETE FROM " + _tableName + " WHERE id = @id", ("@id", nodeId));
}
