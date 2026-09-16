using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Stores;

/// <summary>
/// The last revert window's start, remembered beside the database.
/// <para>A revert window (<see cref="IDataStore.BeginRevertWindow"/>) is somebody saying "this is
/// the state worth coming back to" before changing things. That is exactly the moment the admin
/// UI's "go back in time" wants to offer, and it would be of no use if it vanished when the window
/// was committed - or when the database was closed, which going back in time does itself. So the
/// start of every window is written here, and the newest one stays readable whatever became of the
/// window it belonged to.</para>
/// <para>It lives with the state snapshot, is never read by the store itself, and losing it costs
/// nothing but the suggestion: every read is tolerant and answers null rather than throwing.</para>
/// </summary>
public sealed record RevertMark(long Timestamp, DateTime BegunUtc) {
    /// <summary><see cref="Timestamp"/> as a point in time (log timestamps are UTC ticks).</summary>
    public DateTime Utc => new(Timestamp, DateTimeKind.Utc);

    /// <summary>The last window begun on this database, or null when there has been none this file
    /// knows of - including when it cannot be read at all.</summary>
    public static RevertMark? ReadOrNull(IIOProvider io) {
        try {
            var key = FileKeyUtility.RevertMarkFileKey;
            if (io.DoesNotExistOrIsEmpty(key)) return null;
            var parts = io.ReadAllTextUTF8(key).Split('|');
            if (parts.Length != 2) return null;
            if (!long.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var timestamp)) return null;
            if (timestamp <= 0) return null;
            if (!DateTime.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var begun)) return null;
            return new RevertMark(timestamp, DateTime.SpecifyKind(begun, DateTimeKind.Utc));
        } catch {
            return null;
        }
    }

    /// <summary>Records this window as the last one. Throws nothing a caller has to handle: a
    /// window must begin whether or not the note beside it could be written.</summary>
    public static void Write(IIOProvider io, RevertMark mark, Action<string, Exception?>? logError = null) {
        try {
            io.WriteAllTextUTF8(FileKeyUtility.RevertMarkFileKey,
                mark.Timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + mark.BegunUtc.ToString("o"));
        } catch (Exception error) {
            logError?.Invoke("Could not record the start of the revert window. It will not be offered as a point to go back to. ", error);
        }
    }
}
