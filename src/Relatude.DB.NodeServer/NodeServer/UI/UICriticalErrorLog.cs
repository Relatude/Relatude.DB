using System.Globalization;
using Relatude.DB.IO;

namespace Relatude.DB.NodeServer.UI;

sealed class UICriticalErrorLog {
    const string entryStart = "#######START CRITICAL ERROR LOG########";
    const int maxBytesRead = 4 * 1024 * 1024;
    static readonly string[] fileKey = FileKeyUtility.CriticalErrorLogFileKey;
    readonly RelatudeDBServer _server;
    readonly Dictionary<Guid, Summary> _summaries = [];
    internal UICriticalErrorLog(RelatudeDBServer server) => _server = server;

    internal void Register(UICommands commands) {
        commands.Register("critical-error-log-read", ctx => read(ctx.Payload<StorePayload>().StoreId));
        commands.Register("critical-error-log-clear", ctx => clear(ctx.Payload<StorePayload>().StoreId));
    }

    internal List<object> List(IEnumerable<NodeStoreContainer> containers) {
        var list = new List<object>();
        foreach (var sharing in containers.GroupBy(UICustomLogs.logIoId)) {
            if (sharing.Key is not Guid ioId) continue;
            try {
                if (!_server.TryGetIO(ioId, out var io)) continue;
                var summary = summarize(ioId, io);
                if (summary.SizeBytes == 0) continue;
                var first = sharing.First();
                list.Add(describe(first, ioId, summary, sharing.Select(nameOf).ToArray()));
            } catch { }
        }
        return list;
    }

    object read(Guid storeId) {
        var (c, ioId, io) = logOf(storeId);
        var size = io.GetFileSizeOrZeroIfUnknown(fileKey);
        var text = size == 0 ? string.Empty : readTail(io, size);
        var summary = summarizeText(text, size);
        lock (_summaries) _summaries[ioId] = summary;
        return new {
            Log = describe(c, ioId, summary, [nameOf(c)]),
            ShownBytes = Math.Min(size, maxBytesRead),
            Text = text,
        };
    }

    object clear(Guid storeId) {
        var (_, ioId, io) = logOf(storeId);
        io.DeleteFileIfItExists(fileKey);
        lock (_summaries) _summaries.Remove(ioId);
        return new { Cleared = true };
    }

    (NodeStoreContainer Container, Guid IoId, IIOProvider Io) logOf(Guid storeId) {
        if (!_server.Containers.TryGetValue(storeId, out var c)) throw new Exception("Database not found. ");
        if (UICustomLogs.logIoId(c) is not Guid ioId || !_server.TryGetIO(ioId, out var io)) throw new Exception("The database has no storage for its logs. ");
        return (c, ioId, io);
    }

    Summary summarize(Guid ioId, IIOProvider io) {
        var size = io.GetFileSizeOrZeroIfUnknown(fileKey);
        lock (_summaries) {
            if (_summaries.TryGetValue(ioId, out var known) && known.SizeBytes == size) return known;
        }
        var summary = size == 0 ? new Summary(0, 0, true, null) : summarizeText(readTail(io, size), size);
        lock (_summaries) _summaries[ioId] = summary;
        return summary;
    }

    static string readTail(IIOProvider io, long size) {
        var start = Math.Max(0, size - maxBytesRead);
        string text;
        using (var stream = io.OpenRead(fileKey, start)) {
            text = stream.ReadUTF8StringNoLengthPrefix((int)Math.Min(maxBytesRead, stream.Length - start));
        }
        if (start == 0) return text;
        var firstWhole = text.IndexOf(entryStart, StringComparison.Ordinal);
        return firstWhole > 0 ? text[firstWhole..] : text;
    }

    static Summary summarizeText(string text, long size) {
        var entries = 0;
        DateTime? latest = null;
        var at = text.IndexOf(entryStart, StringComparison.Ordinal);
        while (at >= 0) {
            entries++;
            latest = timestampAfter(text, at + entryStart.Length) ?? latest;
            at = text.IndexOf(entryStart, at + entryStart.Length, StringComparison.Ordinal);
        }
        return new Summary(size, entries, size <= maxBytesRead, latest);
    }

    static DateTime? timestampAfter(string text, int from) {
        var rest = text.AsSpan(from, Math.Min(100, text.Length - from)).TrimStart();
        var end = rest.IndexOfAny(' ', '\r', '\n');
        var token = end < 0 ? rest : rest[..end];
        return DateTime.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var utc) ? utc.ToUniversalTime() : null;
    }

    static object describe(NodeStoreContainer c, Guid ioId, Summary summary, string[] databases) {
        var io = c.Settings.IOSettings?.FirstOrDefault(s => s.Id == ioId);
        return new {
            StoreId = c.Settings.Id,
            Databases = databases,
            FileKey = fileKey.AsKeyString(),
            Storage = io == null ? null : string.IsNullOrEmpty(io.Name) ? io.IOType.ToString() : io.Name,
            summary.SizeBytes,
            summary.Entries,
            summary.Complete,
            summary.LatestUtc,
        };
    }

    static string nameOf(NodeStoreContainer c) => string.IsNullOrEmpty(c.Settings.Name) ? c.Settings.Id.ToString() : c.Settings.Name;

    sealed record Summary(long SizeBytes, int Entries, bool Complete, DateTime? LatestUtc);
    sealed record StorePayload(Guid StoreId);
}
