using System.Text;
using System.Text.Json;
using Relatude.DB.Datamodels;
using Relatude.DB.IO;

namespace Relatude.DB.NodeServer.ModelEditor;

/// <summary>The model being edited in the admin UI. There is one per database.</summary>
public sealed class DatamodelDraft {
    public DateTime SavedUtc { get; set; }
    /// <summary>The checksum of the model as saved (see <see cref="DatamodelJson.Checksum"/>), or of
    /// the raw model when it cannot be initialized yet.</summary>
    public Guid Checksum { get; set; }
    /// <summary>The checksum of the active model the draft was started from, so the editor can tell
    /// when the active model has moved on under it.</summary>
    public Guid? BaseChecksum { get; set; }
    /// <summary>
    /// Set when activation wrote the draft into compiled sources: the model can only become active
    /// once the application is rebuilt and restarted. The draft is kept until a store opens with a
    /// model of the same checksum, and then deleted.
    /// </summary>
    public bool AwaitingRebuild { get; set; }
    public DateTime? AwaitingRebuildSinceUtc { get; set; }
    public string? Note { get; set; }
    public required Datamodel Model { get; set; }
}

/// <summary>One model that has been active, as kept in the history folder.</summary>
public sealed class DatamodelHistoryEntry {
    /// <summary>The joined file key, which is how the UI names the entry.</summary>
    public required string Key { get; set; }
    public DateTime SavedUtc { get; set; }
    /// <summary>Why it was saved: "open" (found active when the database opened), "replaced" (the
    /// model an activation replaced) or "activated".</summary>
    public string Reason { get; set; } = "";
    public Guid Checksum { get; set; }
    public int NodeTypes { get; set; }
    public int Relations { get; set; }
    public int Properties { get; set; }
    public long Size { get; set; }
    /// <summary>Only set by <see cref="DatamodelDrafts.LoadHistory"/>; the listing leaves it out.</summary>
    public Datamodel? Model { get; set; }
}

/// <summary>
/// The datamodel editor's files of one database, kept in the <see cref="FileKeyUtility.DatamodelsFolderName"/>
/// folder of the database's primary IO provider: the draft being edited, and a history of every model
/// that has been active. Every file is a JSON envelope - a few header fields, then the model in the
/// same form JsonFile sources use (<see cref="DatamodelJson"/>) - so a history file can be read back
/// as a model by anything that reads a model file.
///
/// The history is written by <see cref="Snapshot"/> whenever a store opens with a model the newest
/// entry does not already hold, and just before an activation replaces a model. So the newest entry
/// is normally the active model, older entries are what to go back to, and a model changed by a
/// developer in code and restarted lands in the history like any other. Only the newest
/// <see cref="HistoryRetention"/> entries are kept.
/// </summary>
public sealed class DatamodelDrafts {
    public const int HistoryRetention = 50;
    readonly IIOProvider _io;
    readonly object _lock = new();
    public DatamodelDrafts(IIOProvider io) => _io = io;

    // ---- the draft ----

    public bool HasDraft => _io.ExistsAndIsNotEmpty(FileKeyUtility.Datamodel_DraftFileKey);

    public DatamodelDraft? LoadDraft() {
        lock (_lock) {
            var key = FileKeyUtility.Datamodel_DraftFileKey;
            if (!_io.ExistsAndIsNotEmpty(key)) return null;
            var text = _io.ReadAllTextUTF8(key);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            return new DatamodelDraft {
                SavedUtc = utc(root, "SavedUtc") ?? DateTime.UtcNow,
                Checksum = guid(root, "Checksum") ?? Guid.Empty,
                BaseChecksum = guid(root, "BaseChecksum"),
                AwaitingRebuild = root.TryGetProperty("AwaitingRebuild", out var awaiting) && awaiting.ValueKind == JsonValueKind.True,
                AwaitingRebuildSinceUtc = utc(root, "AwaitingRebuildSinceUtc"),
                Note = root.TryGetProperty("Note", out var note) && note.ValueKind == JsonValueKind.String ? note.GetString() : null,
                Model = DatamodelJson.Deserialize(root.GetProperty("Model").GetRawText()),
            };
        }
    }
    /// <summary>Reads the draft's header without deserializing its model, for status displays.</summary>
    public DatamodelDraft? PeekDraft() {
        lock (_lock) {
            var key = FileKeyUtility.Datamodel_DraftFileKey;
            if (!_io.ExistsAndIsNotEmpty(key)) return null;
            var header = readHeader(_io.ReadAllBytes(key));
            return new DatamodelDraft {
                SavedUtc = utc(header, "SavedUtc") ?? DateTime.UtcNow,
                Checksum = guid(header, "Checksum") ?? Guid.Empty,
                BaseChecksum = guid(header, "BaseChecksum"),
                AwaitingRebuild = header.TryGetValue("AwaitingRebuild", out var awaiting) && awaiting.ValueKind == JsonValueKind.True,
                AwaitingRebuildSinceUtc = utc(header, "AwaitingRebuildSinceUtc"),
                Note = header.TryGetValue("Note", out var note) && note.ValueKind == JsonValueKind.String ? note.GetString() : null,
                Model = null!, // not read; callers of PeekDraft only look at the header
            };
        }
    }
    public void SaveDraft(DatamodelDraft draft) {
        lock (_lock) {
            draft.SavedUtc = DateTime.UtcNow;
            var text = writeEnvelope(w => {
                w.WriteString("SavedUtc", draft.SavedUtc);
                w.WriteString("Checksum", draft.Checksum);
                if (draft.BaseChecksum != null) w.WriteString("BaseChecksum", draft.BaseChecksum.Value);
                w.WriteBoolean("AwaitingRebuild", draft.AwaitingRebuild);
                if (draft.AwaitingRebuildSinceUtc != null) w.WriteString("AwaitingRebuildSinceUtc", draft.AwaitingRebuildSinceUtc.Value);
                if (draft.Note != null) w.WriteString("Note", draft.Note);
                writeSummary(w, draft.Model);
            }, draft.Model);
            _io.WriteAllTextUTF8(FileKeyUtility.Datamodel_DraftFileKey, text);
        }
    }
    public void DeleteDraft() {
        lock (_lock) _io.DeleteFileIfItExists(FileKeyUtility.Datamodel_DraftFileKey);
    }

    // ---- the history ----

    /// <summary>Every history entry, newest first, headers only.</summary>
    public List<DatamodelHistoryEntry> ListHistory() {
        lock (_lock) {
            var entries = new List<DatamodelHistoryEntry>();
            foreach (var key in FileKeyUtility.Datamodel_GetAllHistoryFileKeys(_io)) {
                try {
                    var bytes = _io.ReadAllBytes(key);
                    var entry = entryFromHeader(key, readHeader(bytes), bytes.Length);
                    entries.Add(entry);
                } catch {
                    // an unreadable history file is left where it is and left out of the list: it is
                    // somebody's backup, and not a reason to hide the rest
                }
            }
            entries.Reverse();
            return entries;
        }
    }
    public DatamodelHistoryEntry? LoadHistory(string key) {
        lock (_lock) {
            var fileKey = key.SplitKey();
            if (!FileKeyUtility.Datamodel_IsHistoryFileKey(fileKey) || !_io.ExistsAndIsNotEmpty(fileKey)) return null;
            var bytes = _io.ReadAllBytes(fileKey);
            var entry = entryFromHeader(fileKey, readHeader(bytes), bytes.Length);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            entry.Model = DatamodelJson.Deserialize(document.RootElement.GetProperty("Model").GetRawText());
            return entry;
        }
    }
    public bool DeleteHistory(string key) {
        lock (_lock) {
            var fileKey = key.SplitKey();
            if (!FileKeyUtility.Datamodel_IsHistoryFileKey(fileKey) || !_io.Exists(fileKey)) return false;
            _io.DeleteFileIfItExists(fileKey);
            return true;
        }
    }
    /// <summary>
    /// Adds the model to the history, unless <paramref name="onlyIfChanged"/> and the newest entry
    /// already holds a model with the same checksum. Returns the entry written, or null when skipped.
    /// </summary>
    public DatamodelHistoryEntry? Snapshot(Datamodel model, string reason, bool onlyIfChanged = true) {
        lock (_lock) {
            var checksum = DatamodelJson.Checksum(model);
            var existing = FileKeyUtility.Datamodel_GetAllHistoryFileKeys(_io);
            if (onlyIfChanged && existing.Length > 0) {
                try {
                    var newest = readHeader(_io.ReadAllBytes(existing[^1]));
                    if (guid(newest, "Checksum") == checksum) return null;
                } catch { }
            }
            var now = DateTime.UtcNow;
            var key = FileKeyUtility.Datamodel_GetHistoryFileKey(now);
            // second resolution: two snapshots within one second take the next free second
            for (var i = 1; _io.Exists(key) && i < 60; i++) key = FileKeyUtility.Datamodel_GetHistoryFileKey(now.AddSeconds(i));
            var text = writeEnvelope(w => {
                w.WriteString("SavedUtc", now);
                w.WriteString("Reason", reason);
                w.WriteString("Checksum", checksum);
                writeSummary(w, model);
            }, model);
            _io.WriteAllTextUTF8(key, text);
            // retention: the oldest go first, and only after the new one is safely written
            var all = FileKeyUtility.Datamodel_GetAllHistoryFileKeys(_io);
            for (var i = 0; i < all.Length - HistoryRetention; i++) _io.DeleteFileIfItExists(all[i]);
            var bytes = Encoding.UTF8.GetByteCount(text);
            return entryFromHeader(key, readHeader(Encoding.UTF8.GetBytes(text)), bytes);
        }
    }

    // ---- what happens when a store opens ----

    /// <summary>
    /// Records the model a store just opened with: it goes into the history when it differs from the
    /// newest entry, and a draft that was waiting for a rebuild is done with once the model it wrote
    /// is the one that opened. Never throws; the store is open and this must not take it down.
    /// </summary>
    public static void RecordOpen(NodeStoreContainer container) {
        try {
            var json = container.DatamodelAsLoadedJson;
            if (json == null) return;
            var io = container.Server.GetOrNullIO(container.Settings.IoDatabase);
            if (io == null) return;
            var drafts = new DatamodelDrafts(io);
            var model = DatamodelJson.Deserialize(json);
            model.EnsureInitalization();
            drafts.Snapshot(model, "open");
            var draft = drafts.PeekDraft();
            if (draft != null && draft.AwaitingRebuild && draft.Checksum == DatamodelJson.Checksum(model)) {
                drafts.DeleteDraft();
                container.Store?.Datastore.LogInfo("The data model draft that was waiting for a rebuild is now the active model; the draft was removed. ");
            }
        } catch (Exception error) {
            try { container.Store?.Datastore.LogError("Could not record the data model in the model history: " + error.Message, error); } catch { }
        }
    }

    // ---- the envelope ----

    static string writeEnvelope(Action<Utf8JsonWriter> header, Datamodel model) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })) {
            writer.WriteStartObject();
            header(writer);
            writer.WritePropertyName("Model");
            JsonSerializer.Serialize(writer, model, DatamodelJson.Options);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    static void writeSummary(Utf8JsonWriter w, Datamodel model) {
        var types = model.NodeTypes.Values.Where(t => t.Id != NodeConstants.BaseNodeTypeId).ToList();
        w.WriteStartObject("Summary");
        w.WriteNumber("NodeTypes", types.Count);
        w.WriteNumber("Relations", model.Relations.Count);
        w.WriteNumber("Properties", types.Sum(t => t.Properties.Count));
        w.WriteEndObject();
    }
    /// <summary>The top level properties of an envelope other than the model, which is skipped
    /// rather than parsed: a listing of fifty history files should not deserialize fifty models.</summary>
    static Dictionary<string, JsonElement> readHeader(byte[] bytes) {
        var header = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new Exception("Not a datamodel envelope. ");
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName) {
            var name = reader.GetString()!;
            reader.Read();
            if (name.Equals("Model", StringComparison.OrdinalIgnoreCase)) {
                reader.Skip();
                continue;
            }
            header[name] = JsonElement.ParseValue(ref reader);
        }
        return header;
    }
    static DatamodelHistoryEntry entryFromHeader(string[] key, Dictionary<string, JsonElement> header, long size) {
        var entry = new DatamodelHistoryEntry {
            Key = key.AsKeyString(),
            SavedUtc = utc(header, "SavedUtc") ?? FileKeyUtility.Datamodel_GetHistoryDateTimeFromFileKey(key),
            Reason = header.TryGetValue("Reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() ?? "" : "",
            Checksum = guid(header, "Checksum") ?? Guid.Empty,
            Size = size,
        };
        if (header.TryGetValue("Summary", out var summary) && summary.ValueKind == JsonValueKind.Object) {
            if (summary.TryGetProperty("NodeTypes", out var t) && t.TryGetInt32(out var types)) entry.NodeTypes = types;
            if (summary.TryGetProperty("Relations", out var r) && r.TryGetInt32(out var relations)) entry.Relations = relations;
            if (summary.TryGetProperty("Properties", out var p) && p.TryGetInt32(out var properties)) entry.Properties = properties;
        }
        return entry;
    }
    static DateTime? utc(Dictionary<string, JsonElement> header, string name)
        => header.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.String && e.TryGetDateTime(out var dt) ? dt.ToUniversalTime() : null;
    static DateTime? utc(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String && e.TryGetDateTime(out var dt) ? dt.ToUniversalTime() : null;
    static Guid? guid(Dictionary<string, JsonElement> header, string name)
        => header.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.String && e.TryGetGuid(out var g) ? g : null;
    static Guid? guid(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String && e.TryGetGuid(out var g) ? g : null;
}
