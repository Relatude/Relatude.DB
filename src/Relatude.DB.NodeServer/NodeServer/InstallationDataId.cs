using System.Text.Json;
using Relatude.DB.Common;
using Relatude.DB.IO;

namespace Relatude.DB.NodeServer;

/// <summary>
/// The random part of the installation key (see <see cref="LicenseLogin.DescribeInstallation"/>): an id
/// made the first time it is asked for and kept with the default database, in
/// installation/installation.json at the root of the storage provider that database keeps its files
/// in - beside data/, state/ and log/ on local disk, in the blob container when the database is in one.
///
/// <para>It is what tells two applications apart when everything else about them is the same. One
/// relatude.db.json published to two apps, or copied to a second site, carries one server id, and the
/// host part of the key is the same for every application on one machine. Each application keeps its
/// own data, so each finds its own id there. It goes wherever the data goes - across restarts,
/// redeploys and a move to another machine or App Service worker - which is what an installation is.
/// A copy of the data carries it along; the host part tells such a copy apart unless it runs on the
/// same host.</para>
///
/// <para>A database in memory storage keeps nothing, so then - and when there is no database at all -
/// the id is kept on disk below the root data folder instead, at <see cref="FallbackRelativePath"/>.
/// When the place cannot be read or written (a read-only deployment, blob storage that does not
/// answer) there is no id: the key goes without it, and the place is tried again a minute later.</para>
///
/// <para>Found once and then kept for the life of the process, so the key does not change under a
/// running server when the default database is changed in the admin UI; a soft restart finds it
/// again (<see cref="Forget"/>).</para>
/// </summary>
sealed class InstallationDataId(RelatudeDBServer server) {
    public const string FileName = "installation.json";
    /// <summary>The key of the file in the default database's storage provider.</summary>
    public static readonly string[] FileKey = [FileKeyUtility.InstallationFolderName, FileName];
    /// <summary>Where the file is kept when the default database keeps nothing on disk or in blob
    /// storage, relative to the root data folder.</summary>
    public static readonly string FallbackRelativePath = Path.Combine(Defaults.DataFolderPath, FileKeyUtility.InstallationFolderName, FileName);

    /// <summary>The id, or null with the <see cref="Problem"/> that kept it from being read or kept.
    /// <see cref="Place"/> says where it is kept, for people.</summary>
    public sealed record Found(string? Id, string Place, string? Problem);

    sealed record FileContent(string? Id, DateTime CreatedUtc);
    static readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    static readonly TimeSpan _retryAfter = TimeSpan.FromMinutes(1);

    readonly object _lock = new();
    Found? _found;
    DateTime _triedUtc;

    public Found Get() {
        lock (_lock) {
            if (_found != null && (_found.Id != null || DateTime.UtcNow - _triedUtc < _retryAfter)) return _found;
            _triedUtc = DateTime.UtcNow;
            _found = find();
            if (_found.Problem != null) server.Log("No installation id could be kept in " + _found.Place + ": " + _found.Problem);
            return _found;
        }
    }

    /// <summary>Finds the id again at the next <see cref="Get"/>: the settings were read again, and may
    /// name another default database.</summary>
    public void Forget() {
        lock (_lock) _found = null;
    }

    Found find() {
        var settings = server.Settings;
        var containers = settings.ContainerSettings ?? [];
        var container = containers.FirstOrDefault(c => c.Id == settings.DefaultStoreId) ?? containers.FirstOrDefault();
        var ioSettings = container?.IoDatabase is Guid ioId ? (container.IOSettings ?? []).FirstOrDefault(s => s.Id == ioId) : null;
        if (container != null && ioSettings != null && ioSettings.IOType != IOTypes.Memory) {
            var place = FileKey.AsKeyString() + " in the storage of the database \"" + (container.Name ?? container.Id.ToString()) + "\"";
            try {
                var io = server.GetIO(ioSettings.Id);
                if (io is IOProviderDisk disk) place = displayOf(Path.Combine(disk.BaseFolder, FileKeyUtility.InstallationFolderName, FileName));
                var id = readOrCreate(
                    () => io.ExistsAndIsNotEmpty(FileKey) ? io.ReadAllTextUTF8(FileKey) : null,
                    text => io.WriteAllTextUTF8(FileKey, text));
                return new Found(id, place, null);
            } catch (Exception err) {
                return new Found(null, place, err.Message);
            }
        }
        var path = Path.GetFullPath(Path.Combine(server.RootDataFolderPath, FallbackRelativePath));
        try {
            var id = readOrCreate(
                () => File.Exists(path) ? FileOpenRetry.Open(path, () => File.ReadAllText(path)) : null,
                text => {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, text);
                });
            return new Found(id, displayOf(path), null);
        } catch (Exception err) {
            return new Found(null, displayOf(path), err.Message);
        }
    }

    /// <summary>
    /// The id in the file, or a new one written to it when there is no file or nothing usable in it.
    /// What is written is read back and used, so two processes starting at once on a new database
    /// (an overlapped recycle) end up on the one id that is in the file.
    /// </summary>
    static string readOrCreate(Func<string?> read, Action<string> write) {
        if (idIn(read()) is { } existing) return existing;
        var created = SecureGuid.New().ToString("N");
        write(JsonSerializer.Serialize(new FileContent(created, DateTime.UtcNow), _json));
        return idIn(read()) ?? created;
    }

    static string? idIn(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try {
            var content = JsonSerializer.Deserialize<FileContent>(text, _json);
            return Guid.TryParse(content?.Id, out var id) && id != Guid.Empty ? id.ToString("N") : null;
        } catch (JsonException) {
            return null; // a file broken by hand: a new id replaces it, as there is no other to be had
        }
    }

    string displayOf(string fullPath) {
        var root = server.RootDataFolderPath;
        if (string.IsNullOrEmpty(root)) return fullPath;
        var relative = Path.GetRelativePath(root, fullPath);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? fullPath : relative.Replace('\\', '/');
    }
}
