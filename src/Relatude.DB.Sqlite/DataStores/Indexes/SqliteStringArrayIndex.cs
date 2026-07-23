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
    protected override IEnumerable<KeyValuePair<int, string[]>> ReadAllPersisted() {
        using var cmd = _store.CreateCommand("SELECT id, value FROM " + _tableName);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) yield return new(reader.GetInt32(0), JsonSerializer.Deserialize<string[]>(reader.GetString(1))!);
    }
    protected override void PersistAdd(int nodeId, string[] value) {
        using var cmd = _store.CreateCommand("INSERT INTO " + _tableName + " (id, value) VALUES (@id, @value)");
        cmd.Parameters.AddWithValue("@id", nodeId);
        cmd.Parameters.AddWithValue("@value", JsonSerializer.Serialize(value));
        cmd.ExecuteNonQuery();
    }
    protected override void PersistRemove(int nodeId) {
        using var cmd = _store.CreateCommand("DELETE FROM " + _tableName + " WHERE id = @id");
        cmd.Parameters.AddWithValue("@id", nodeId);
        cmd.ExecuteNonQuery();
    }
}
