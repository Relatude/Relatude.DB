using Microsoft.Data.Sqlite;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.AI;
public class SqlLiteEmbeddingCache : IEmbeddingCache {
    readonly object _lock = new();
    SqliteConnection _cn = default!;
    string? _localFilePath;
    bool _open = false;
    void openIfClosed() {
        lock (_lock) {
            if (_cn != null) return;
            var connectionStr = _localFilePath == null ? "Data Source=:memory:" : $"Data Source={_localFilePath}";
            _cn = new();
            _cn.ConnectionString = connectionStr;
            _cn.Open();
            using var pragma = _cn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL";
            pragma.ExecuteNonQuery();
            using var command = _cn.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS Embeddings (Hash TEXT PRIMARY KEY, Embedding BLOB)";
            command.ExecuteNonQuery();
            _open = true;
        }
    }
    public SqlLiteEmbeddingCache(string? localFilePath) {
        _localFilePath = localFilePath;
    }
    public void Set(ulong hash, float[] embedding) {
        lock (_lock) {
            openIfClosed();
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO Embeddings (Hash, Embedding) VALUES (@Hash, @Embedding)";
            cmd.Parameters.AddWithValue("@Hash", hash.ToString());
            cmd.Parameters.AddWithValue("@Embedding", toBytes(embedding));
            cmd.ExecuteNonQuery();
        }

    }
    public void SetMany(IEnumerable<Tuple<ulong, float[]>> values) {
        lock (_lock) {
            openIfClosed();
            using var transaction = _cn.BeginTransaction();
            foreach (var value in values) {
                using var cmd = _cn.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO Embeddings (Hash, Embedding) VALUES (@Hash, @Embedding)";
                cmd.Parameters.AddWithValue("@Hash", value.Item1.ToString());
                cmd.Parameters.AddWithValue("@Embedding", toBytes(value.Item2));
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }
    byte[] toBytes(float[] embedding) {
        var bytes = new byte[embedding.Length * 4];
        for (int i = 0; i < embedding.Length; i++) {
            var f = embedding[i];
            var b = BitConverter.GetBytes(f);
            Array.Copy(b, 0, bytes, i * 4, 4);
        }
        return bytes;
    }
    float[] toFloats(byte[] bytes) {
        var floats = new float[bytes.Length / 4];
        for (int i = 0; i < floats.Length; i++) {
            floats[i] = BitConverter.ToSingle(bytes, i * 4);
        }
        return floats;
    }
    public bool TryGet(ulong hash, [MaybeNullWhen(false)] out float[] embedding) {
        lock (_lock) {
            openIfClosed();
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "SELECT Embedding FROM Embeddings WHERE Hash = @Hash";
            cmd.Parameters.AddWithValue("@Hash", hash.ToString());
            using var reader = cmd.ExecuteReader();
            if (reader.Read()) {
                embedding = toFloats(reader.GetFieldValue<byte[]>(0));
                return true;
            }
        }
        embedding = null;
        return false;
    }
    public void ClearAll() {
        lock (_lock) {
            openIfClosed();
            using var cmd = _cn.CreateCommand();
            cmd.CommandText = "DELETE FROM Embeddings";
            cmd.ExecuteNonQuery();
        }
    }
    public void Dispose() {
        if (_open) _cn.Close();
        _open = false;
        if (_cn == null) return;
        _cn.Dispose();
        _cn = null!;
    }
}

