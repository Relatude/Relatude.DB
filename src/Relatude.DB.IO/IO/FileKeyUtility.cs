using Relatude.DB.Common;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.IO;
/// <summary>
/// A common utility class for generating file keys and making sure naming is consistent and that the different stores do have conflicting names.
/// A file key is a string array: each element is a folder name and the last element is the file name.
/// The first part of the file name, separated by a dot, denotes the store type.
/// The last part, separated by a dot, (file extension) denotes the file type:
///     .bin = binary file
///     .txt = text file
///     .bkup = backup file
///
/// The middle part, separated by dots, is used for numbering or date/time stamps etc depending on the store
/// The '-' char is special and reserved for delimiting date/time parts.
/// The prefix can be used to separate different database instances in the same storage location.
///
/// Files are grouped in a set of well known folders below the storage root:
///     data/   = the database log files (primary and secondary)
///     state/  = everything rebuildable from the log: state snapshot, memory index states, mapper dll, persisted queue
///     backup/ = backup files
///     log/    = the system logger files and the critical error log
/// The folder names are plain (no prefix); the instance prefix stays on the file name inside them,
/// so several instances can still share one storage location. The prefixed indexes folder and the
/// multi file store folder are unchanged.
///
/// Wildcard patterns are file keys with wildcards in their segments, matched segment by segment
/// (<see cref="FileKeyExtensions.MatchesPattern"/>). Listings (<see cref="FileMeta.Key"/>) carry
/// the joined ('/'-separated) form of a key.
/// </summary>
public static class FileKeyUtility {

    public const string DataFolderName = "data";
    public const string StateFolderName = "state";
    public const string BackupFolderName = "backup";
    public const string LogFolderName = "log";
    /// <summary>
    /// The folder the datamodel editor keeps its files in: the draft model being edited, and the
    /// history of every model that has been active (see the Datamodel_* methods below).
    /// </summary>
    public const string DatamodelsFolderName = "datamodels";
    /// <summary>The folder the file conversion engine caches its converted files in. Not a system
    /// folder: like the index and file store folders it owns its content and is not listed by
    /// <see cref="IIOProvider.GetFiles"/>.</summary>
    public const string ConvertedFolderName = "converted";
    public static readonly string[] SystemFolderNames = [DataFolderName, StateFolderName, BackupFolderName, LogFolderName, DatamodelsFolderName];

    /// <summary>
    /// The folders below the storage root holding data that exists nowhere else: the database log
    /// files and the multi file store. Everything else under the root is either a copy of these
    /// (backup) or derived from them and rebuilt on demand (state, indexes, converted, log), so
    /// only these two cannot be regenerated once they are gone.
    /// </summary>
    public static readonly string[] PrimaryDataFolderNames = [DataFolderName, multiFileStoreFolderPattern];

    /// <summary>Whether the folder at this relative path is a primary data folder or sits below
    /// one; see <see cref="PrimaryDataFolderNames"/>. The path is relative to the storage root.</summary>
    public static bool IsPrimaryDataFolder(string relpath) {
        var key = relpath.SplitKey();
        return key.Length > 0 && PrimaryDataFolderNames.Contains(key[0], StringComparer.OrdinalIgnoreCase);
    }

    static readonly HashSet<string> storeNames = ["db", "files", "index", "ai", "log", "mapper", "queue"]; // starting with these are reserved
    // patterns are file keys with wildcards in their segments, matched segment by segment
    static readonly string[] walSecondaryFilePattern = [DataFolderName, "db.log"];
    static readonly string[] walFilePattern = [DataFolderName, "db.*.bin"];
    static readonly string[] walFileBackupPattern = [BackupFolderName, "db.*.bkup"];
    static readonly string[] walFileBackupPatternKeepForever = [BackupFolderName, "db.bkup.keep.*.bkup"];

    const string multiFileStoreFolderPattern = "files";
    static readonly string[] fileStorePattern = ["files.*.bin"];
    static readonly string[] fileStoreBackupPattern = [BackupFolderName, "files.*.bkup"];
    static readonly string[] fileStoreBackupPatternKeepForever = [BackupFolderName, "files.bkup.keep.*.bkup"];

    const string dateTimeTemplate = "yyyy-MM-dd-HH-mm-ss";
    const string dateOnlyTemplate = "yyyy-MM-dd";
    // State snapshots and index states are numbered like the log files (state.00000001.bin,
    // index.[id].00000001.bin): every save writes a NEW numbered file ending with the completion
    // marker, and the older files are deleted only after the new one is complete, so a shutdown
    // mid-write cannot lose the last good state. Numbered names rather than write-and-rename,
    // because some IO providers cannot rename files. The unnumbered legacy name below was written
    // by older versions; legacy files are not read but deleted at open (the store then rebuilds
    // from the log and saves fresh numbered files).
    static readonly string[] stateFileLegacyPattern = [StateFolderName, "state.bin"];
    static readonly string[] stateFilePattern = [StateFolderName, "state.*.bin"];
    static readonly string[] indexFilePattern = [StateFolderName, "index.*.bin"]; // matches numbered and legacy index state files

    // the ai cache lives in the indexes folder; that folder is already prefixed, so like the index
    // engine files below it the file name itself carries no prefix
    const string indexStoreFolderPattern = "indexes";
    static readonly string[] aiCacheFilePattern = [indexStoreFolderPattern, "ai.cache.bin"];
    static readonly string[] aiCacheNativeFilePattern = [indexStoreFolderPattern, "native.ai.cache.bin"];

    static readonly string[] mapperDllFilePattern = [StateFolderName, "mapper.*.dll"];

    static readonly string[] loggerAllFilePattern = [LogFolderName, "log.*"];
    const string loggerNamePrefix = "log"; // the file name part; logger files live in the log folder
    const string loggerFilePartDelim = ".";
    const string loggerStatisticsSuffix = "statistics";
    const string loggerBinaryExt = ".bin";
    const string loggerTextExt = ".txt";
    const string loggerBkUpExt = ".bkup";

    static readonly string[] criticalErrorLogFilePattern = [LogFolderName, "critical.error.txt"];

    const string queueFileName = "queue";
    static readonly string[] queueFileKeyPattern = [StateFolderName, "queue.*"];

    // The datamodel editor's files. The draft is the one model being edited (one per database);
    // history files are timestamped copies of every model that has been active, newest last.
    static readonly string[] datamodelDraftFileKey = [DatamodelsFolderName, "datamodel.draft.json"];
    static readonly string[] datamodelHistoryFilePattern = [DatamodelsFolderName, "datamodel.*.json"];

    /// <summary>The key a pattern describes, with the wildcard in its file name filled in.</summary>
    static string[] fill(string[] pattern, string value) => [.. pattern[..^1], pattern[^1].Replace("*", value)];

    // Storage names of the persisted index engines. These live on the local disk below
    // IndexStoreFolderKey and carry no prefix of their own: that folder is already prefixed, and
    // each engine owns one subfolder below it, which is what lets several engines share one index
    // folder (e.g. values in nativekv, text in lucene). Renaming any of them orphans existing
    // index data, so the engine rebuilds from the log on the next open.
    const string indexEngineNativeKvFolder = "nativekv";
    const string indexEngineNativeKvFile = "nativekv.db";
    const string indexEngineFacetSetsFile = "facetsets.bin";
    const string indexEngineSqliteFolder = "sqlite";
    const string indexEngineSqliteFile = "index.db";
    const string indexEngineLuceneFolder = "lucene";
    const string indexEngineLuceneWalIdFile = "engine.walid";
    const string indexEngineTextIndexFolder = "textindex";
    const string indexEngineTextIndexWalIdFile = "engine.walid";
    const string indexEngineVectorIndexFolder = "vectorindex";
    const string binaryExtension = ".bin";
    const string tempExtension = ".tmp";

    public static string MultiFileStoreFolderKey => multiFileStoreFolderPattern;

    /// <summary>
    /// Hardcoded marker written as the very last 16 bytes of every numbered state and index state
    /// file. It is the last thing written, so a numbered file that does not end with it was
    /// interrupted mid-write and is deleted at the next store open, which then falls back to the
    /// previous complete file (older files are only deleted once a new file is complete).
    /// </summary>
    public static readonly Guid StateFileCompletionMarker = new("f3a97d2c-51b6-4b8e-9d04-7e2c8a61b5f9");
    /// <summary>Whether the file ends with <see cref="StateFileCompletionMarker"/>, i.e. was completely written.</summary>
    public static bool EndsWithStateFileCompletionMarker(IIOProvider io, string[] fileKey) {
        var size = io.GetFileSizeOrZeroIfUnknown(fileKey);
        if (size < 16) return false;
        using var stream = io.OpenRead(fileKey, size - 16);
        return stream.ReadGuid() == StateFileCompletionMarker;
    }
    /// <summary>The number part of a numbered file name: digits only, between the last two dots.</summary>
    static bool hasNumberedFileName(string[] key, int nameParts) {
        var parts = key.FileName().Split('.');
        if (parts.Length != nameParts) return false;
        var number = parts[^2];
        return number.Length > 0 && number.All(char.IsAsciiDigit);
    }

    /// <summary>The unnumbered state snapshot written by older versions. Not read anymore: it is
    /// deleted at open, and the store rebuilds from the log and saves fresh numbered files.</summary>
    public static string[] State_LegacyFileKey => stateFileLegacyPattern;
    public static string[] State_GetFileKey(int n) => fill(stateFilePattern, n.ToString("00000000"));
    public static bool State_IsNumberedFileKey(string[] key) => key.MatchesPattern(stateFilePattern) && hasNumberedFileName(key, 3);
    /// <summary>Whether the key names a state snapshot file, numbered or legacy.</summary>
    public static bool State_IsStateFileKey(string[] key) => key.IsSameKey(stateFileLegacyPattern) || State_IsNumberedFileKey(key);
    public static string[][] State_GetNumberedFileKeys(IIOProvider io) => [.. io.Search(stateFilePattern).Where(k => hasNumberedFileName(k, 3))];
    /// <summary>All state snapshot files, oldest first: the legacy unnumbered file (if present), then the numbered files.</summary>
    public static string[][] State_GetAllFileKeys(IIOProvider io) {
        List<string[]> keys = [];
        if (io.Exists(stateFileLegacyPattern)) keys.Add(stateFileLegacyPattern);
        keys.AddRange(State_GetNumberedFileKeys(io));
        return [.. keys];
    }
    public static string[]? State_GetNewestFileKey(IIOProvider io) => State_GetNumberedFileKeys(io).LastOrDefault();
    public static string[] State_NextFileKey(IIOProvider io) => State_NextFileKey(State_GetAllFileKeys(io));
    public static string[] State_NextFileKey(string[][] existingKeys) {
        var lastNumbered = existingKeys.LastOrDefault(State_IsNumberedFileKey);
        return lastNumbered == null ? State_GetFileKey(1) : nextFileKey(lastNumbered, State_GetFileKey);
    }
    public static void State_DeleteAll(IIOProvider io) {
        foreach (var key in State_GetAllFileKeys(io)) io.DeleteFileIfItExists(key);
    }
    public static string[]? GetAiCacheFileKey(AIProviderCacheType? cacheProvider) {
        if (cacheProvider == null) return null;
        return cacheProvider.Value switch {
            AIProviderCacheType.None => null,
            AIProviderCacheType.Native => aiCacheNativeFilePattern,
            AIProviderCacheType.Memory => null,
            AIProviderCacheType.Sqlite => aiCacheFilePattern,
            _ => throw new NotImplementedException(),
        };
    }
    public static string IndexStoreFolderKey => indexStoreFolderPattern;

    public static string[] CriticalErrorLogFileKey => criticalErrorLogFilePattern;

    public static DateOnly SystemLog_GetFileDateTimeFromFileKey(string[] fileKey) {
        var parts = fileKey.FileName().Split('.');
        var dtSection = parts[^2];
        return DateOnly.ParseExact(dtSection, dateOnlyTemplate, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The unnumbered index state file written by older versions. Not read anymore: it is
    /// deleted at open, and the index rebuilds from the log. An index id never contains a dot
    /// (guid + optional culture/sub key, '_'-separated), which is what keeps the numbered and
    /// legacy names distinguishable.</summary>
    public static string[] Index_GetLegacyFileKey(string indexId) => fill(indexFilePattern, indexId);
    public static string[] Index_GetFileKey(string indexId, int n) => fill(indexFilePattern, indexId + "." + n.ToString("00000000"));
    public static bool Index_IsNumberedFileKey(string[] key) => key.MatchesPattern(indexFilePattern) && hasNumberedFileName(key, 4);
    /// <summary>All state files of one index, oldest first: the legacy unnumbered file (if present), then the numbered files.</summary>
    public static string[][] Index_GetAllFileKeys(IIOProvider io, string indexId) {
        List<string[]> keys = [];
        var legacy = Index_GetLegacyFileKey(indexId);
        if (io.Exists(legacy)) keys.Add(legacy);
        keys.AddRange(io.Search(fill(indexFilePattern, indexId + ".*")).Where(k => hasNumberedFileName(k, 4)));
        return [.. keys];
    }
    public static string[]? Index_GetNewestFileKey(IIOProvider io, string indexId) => Index_GetAllFileKeys(io, indexId).Where(Index_IsNumberedFileKey).LastOrDefault();
    public static string[] Index_NextFileKey(string indexId, string[][] existingKeys) {
        var lastNumbered = existingKeys.LastOrDefault(Index_IsNumberedFileKey);
        return lastNumbered == null ? Index_GetFileKey(indexId, 1) : nextFileKey(lastNumbered, n => Index_GetFileKey(indexId, n));
    }
    /// <summary>Every index state file of every index, numbered and legacy.</summary>
    public static string[][] Index_GetAll(IIOProvider io) {
        return [.. io.Search(indexFilePattern)];
    }
    public static string[][] Index_GetAllNumbered(IIOProvider io) => [.. io.Search(indexFilePattern).Where(k => hasNumberedFileName(k, 4))];


    public static string[] WAL_GetFileKey(int n) => fill(walFilePattern, n.ToString("00000000"));
    public static string[][] WAL_GetAllFileKeys(IIOProvider io) => [.. io.Search(walFilePattern)];
    public static string[] WAL_GetSecondaryFileKey() => walSecondaryFilePattern;
    public static string[] WAL_GetLatestFileKey(IIOProvider io) => WAL_GetAllFileKeys(io).LastOrDefault() ?? WAL_GetFileKey(1);
    public static string[] WAL_NextFileKey(IIOProvider io) => nextFileKey(WAL_GetLatestFileKey(io), WAL_GetFileKey);
    public static DateTime WAL_GetBackUpDateTimeFromFileKey(string[] fileKey) => backupDateTime(fileKey);
    public static string[][] WAL_GetAllBackUpFileKeys(IIOProvider io) => [.. io.Search(walFileBackupPattern)];
    public static string[] WAL_GetFileKeyForBackup(DateTime dt, bool keepForever)
        => fill(keepForever ? walFileBackupPatternKeepForever : walFileBackupPattern, dt.ToString(dateTimeTemplate));
    public static bool WAL_KeepForever(string[] fileKey) => fileKey.MatchesPattern(walFileBackupPatternKeepForever);

    public static string[] FileStore_GetFileKey(int n) => fill(fileStorePattern, n.ToString("00000000"));
    public static string[][] FileStore_GetAllFileKeys(IIOProvider io) => [.. io.Search(fileStorePattern)];
    public static string[] FileStore_GetLatestFileKey(IIOProvider io) => FileStore_GetAllFileKeys(io).LastOrDefault() ?? FileStore_GetFileKey(1);
    public static string[] FileStore_NextFileKey(IIOProvider io) => nextFileKey(FileStore_GetLatestFileKey(io), FileStore_GetFileKey);
    public static DateTime FileStore_GetBackUpDateTimeFromFileKey(string[] fileKey) => backupDateTime(fileKey);
    public static string[][] FileStore_GetAllBackUpFileKeys(IIOProvider io) => [.. io.Search(fileStoreBackupPattern)];
    public static string[] FileStore_GetFileKeyForBackup(DateTime dt, bool keepForever)
        => fill(keepForever ? fileStoreBackupPatternKeepForever : fileStoreBackupPattern, dt.ToString(dateTimeTemplate));
    public static bool FileStore_KeepForever(string[] fileKey) => fileKey.MatchesPattern(fileStoreBackupPatternKeepForever);

    static string[] nextFileKey(string[] fileKey, Func<int, string[]> getFileKey) => getFileKey(int.Parse(fileKey.FileName().Split('.')[^2]) + 1);
    static DateTime backupDateTime(string[] fileKey) => DateTime.SpecifyKind(DateTime.ParseExact(fileKey.FileName().Split('.')[^2], dateTimeTemplate, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    public static string[] MapperDll_GetFileKey(ulong hash) => fill(mapperDllFilePattern, hash.ToString());
    public static string[][] MapperDll_GetAllFileKeys(IIOProvider io) => [.. io.Search(mapperDllFilePattern)];

    public static string[] Logger_GetStatistics(string loggerKey) => [LogFolderName, loggerNamePrefix + loggerFilePartDelim + loggerKey + loggerFilePartDelim + loggerStatisticsSuffix + loggerBinaryExt];
    public static string[] Logger_GetStatisticsBackUp(string loggerKey) {
        var key = Logger_GetStatistics(loggerKey);
        return [.. key[..^1], key.FileName() + loggerBkUpExt];
    }
    /// <summary>The file name prefix (inside the log folder) of one log's files for the given interval.</summary>
    public static string Logger_NamePrefix(string logName, FileInterval fileInterval) => loggerNamePrefix + loggerFilePartDelim + logName + loggerFilePartDelim + fileInterval.ToString().ToLower() + loggerFilePartDelim;
    public static string[] Logger_FileNameBin(string logName, FileInterval fileInterval, DateTime floored) => logger_FileName(logName, fileInterval, floored, loggerBinaryExt);
    public static string[] Logger_FileNameTxt(string logName, FileInterval fileInterval, DateTime floored) => logger_FileName(logName, fileInterval, floored, loggerTextExt);
    public static List<DateTime> Logger_FileDatesBin(IIOProvider io, string logName, FileInterval fileInterval) => getLogFileDates(io, logName, fileInterval, loggerBinaryExt);
    public static List<DateTime> Logger_FileDatesTxt(IIOProvider io, string logName, FileInterval fileInterval) => getLogFileDates(io, logName, fileInterval, loggerTextExt);
    static string[] logger_FileName(string logName, FileInterval fileInterval, DateTime floored, string fileExt) => [LogFolderName, (Logger_NamePrefix(logName, fileInterval) + (fileInterval switch {
            FileInterval.Minute => floored.ToString("yyyy-MM-dd-HH-mm"),
            FileInterval.Hour => floored.ToString("yyyy-MM-dd-HH"),
            FileInterval.Day => floored.ToString("yyyy-MM-dd"),
            FileInterval.Month => floored.ToString("yyyy-MM"),
            _ => throw new NotImplementedException(),
        })).ToLower() + fileExt];
    static DateTime logger_ParseFileName(string fileName, string logName, FileInterval fileInterval, string fileExt) {
        var datePart = fileName[Logger_NamePrefix(logName, fileInterval).Length..];
        datePart = datePart.Substring(0, datePart.Length - fileExt.Length);
        return DateTime.SpecifyKind(DateTime.ParseExact(datePart, fileInterval switch {
            FileInterval.Minute => "yyyy-MM-dd-HH-mm",
            FileInterval.Hour => "yyyy-MM-dd-HH",
            FileInterval.Day => "yyyy-MM-dd",
            FileInterval.Month => "yyyy-MM",
            _ => throw new NotImplementedException(),
        }, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
    }
    static List<DateTime> getLogFileDates(IIOProvider io, string logName, FileInterval fileInterval, string fileExt) {
        string[] pattern = [LogFolderName, Logger_NamePrefix(logName, fileInterval) + "*" + fileExt];
        return io.Search(pattern).Select(f => logger_ParseFileName(f.FileName(), logName, fileInterval, fileExt)).OrderBy(i => i).ToList();
    }

    public static string[] Queue_GetFileKey(string ext) => [StateFolderName, queueFileName + "." + ext];

    /// <summary>The draft model of the datamodel editor; there is one per database.</summary>
    public static string[] Datamodel_DraftFileKey => datamodelDraftFileKey;
    /// <summary>The history file for a model that was active at the given (UTC) time.</summary>
    public static string[] Datamodel_GetHistoryFileKey(DateTime utc) => fill(datamodelHistoryFilePattern, utc.ToString(dateTimeTemplate));
    /// <summary>Whether the key names a datamodel history file (the draft has the same shape but no timestamp).</summary>
    public static bool Datamodel_IsHistoryFileKey(string[] key) => key.MatchesPattern(datamodelHistoryFilePattern) && !key.IsSameKey(datamodelDraftFileKey)
        && DateTime.TryParseExact(key.FileName().Split('.')[^2], dateTimeTemplate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);
    /// <summary>All datamodel history files, oldest first.</summary>
    public static string[][] Datamodel_GetAllHistoryFileKeys(IIOProvider io) => [.. io.Search(datamodelHistoryFilePattern).Where(Datamodel_IsHistoryFileKey)];
    public static DateTime Datamodel_GetHistoryDateTimeFromFileKey(string[] fileKey) => backupDateTime(fileKey);

    // Before the folder layout every file lived in the storage root. The startup migration moves
    // those files into their folders using the helpers below.
    public static string[][] WAL_GetLegacyRootFileKeys(IIOProvider io) => [.. io.Search(["db.*.bin"])];
    public static string[] WAL_GetLegacyRootSecondaryFileKey() => ["db.log"];
    public static string[] MapLegacyRootFileKeyToDataFolder(string[] legacyRootFileKey) => [DataFolderName, .. legacyRootFileKey];
    /// <summary>Database and file store backups in the storage root; they now live in the bkup folder.</summary>
    public static string[][] Legacy_GetRootBackupFileKeys(IIOProvider io)
        => [.. io.Search(["db.*.bkup"]), .. io.Search(["files.*.bkup"])];
    /// <summary>State snapshot, index states, mapper dlls and queue files in the storage root; they now live in the state folder.</summary>
    public static string[][] Legacy_GetRootStateFileKeys(IIOProvider io)
        => [.. io.Search(["state.bin"]), .. io.Search(["state.*.bin"]), .. io.Search(["index.*.bin"]), .. io.Search(["mapper.*.dll"]), .. io.Search(["queue.*"])];
    /// <summary>Logger files and the critical error log in the storage root; they now live in the log folder.</summary>
    public static string[][] Legacy_GetRootLoggerFileKeys(IIOProvider io)
        => [.. io.Search(["log.*"]), .. io.Search(["critical.error.txt"])];
    /// <summary>
    /// The file name the ai cache had in the root of its local disk folder before it moved into the
    /// prefixed indexes folder (where the prefix left the file name), or null when the cache type
    /// has no file. The cache lives on the local disk outside the IO providers, so this is a plain
    /// file name for the server layer's migration.
    /// </summary>
    public static string? GetLegacyRootAiCacheFileName(AIProviderCacheType? cacheProvider) {
        return cacheProvider switch {
            AIProviderCacheType.Native => "native.ai.cache.bin",
            AIProviderCacheType.Sqlite => "ai.cache.bin",
            _ => null,
        };
    }

    #region Index engine storage (static: no prefix, they sit below IndexStoreFolderKey)

    /// <summary>Folder of the native KV value index engine.</summary>
    public static string IndexEngine_NativeKvFolderKey => indexEngineNativeKvFolder;
    /// <summary>The native KV engine's database file, inside <see cref="IndexEngine_NativeKvFolderKey"/>.</summary>
    public static string IndexEngine_NativeKvFileKey => indexEngineNativeKvFile;
    /// <summary>The native KV engine's facet-set sidecar, inside <see cref="IndexEngine_NativeKvFolderKey"/>.</summary>
    public static string IndexEngine_FacetSetsFileKey => indexEngineFacetSetsFile;
    /// <summary>Folder of the SQLite index engine (value indexes, and the FTS5 word indexes when it serves text too).</summary>
    public static string IndexEngine_SqliteFolderKey => indexEngineSqliteFolder;
    /// <summary>The SQLite engine's database file, inside <see cref="IndexEngine_SqliteFolderKey"/>.</summary>
    public static string IndexEngine_SqliteFileKey => indexEngineSqliteFile;
    /// <summary>Folder of the Lucene text index engine; each word index gets a subfolder below it.</summary>
    public static string IndexEngine_LuceneFolderKey => indexEngineLuceneFolder;
    /// <summary>The Lucene engine's WAL-file-id marker, inside <see cref="IndexEngine_LuceneFolderKey"/>.</summary>
    public static string IndexEngine_LuceneWalIdFileKey => indexEngineLuceneWalIdFile;
    /// <summary>
    /// Folder of one Lucene word index, below <see cref="IndexEngine_LuceneFolderKey"/>. A word
    /// index id is already unique per property, culture and sub key, so the id itself names the
    /// folder; it is lowercased so the name is identical on case sensitive and case insensitive
    /// file systems. Invariant lowercasing, because the current culture must not decide the folder
    /// name: under a Turkish locale a culture code such as "it-IT" would lowercase to "ıt-ıt", and
    /// the index would look fresh (and be rebuilt) on the next open.
    /// </summary>
    public static string IndexEngine_LuceneIndexFolderKey(string indexId) => indexId.ToLowerInvariant();
    /// <summary>Folder of the built-in disk text index engine; each word index gets a subfolder below it.</summary>
    public static string IndexEngine_TextIndexFolderKey => indexEngineTextIndexFolder;
    /// <summary>The disk text index engine's WAL-file-id marker, inside <see cref="IndexEngine_TextIndexFolderKey"/>.</summary>
    public static string IndexEngine_TextIndexWalIdFileKey => indexEngineTextIndexWalIdFile;
    /// <summary>Folder of one disk text word index, below <see cref="IndexEngine_TextIndexFolderKey"/>.
    /// Same naming rule as <see cref="IndexEngine_LuceneIndexFolderKey"/> and for the same reasons.</summary>
    public static string IndexEngine_TextIndexIndexFolderKey(string indexId) => indexId.ToLowerInvariant();
    /// <summary>Folder of the built-in disk vector index engine; each semantic index gets a subfolder below it.</summary>
    public static string IndexEngine_VectorIndexFolderKey => indexEngineVectorIndexFolder;
    /// <summary>Folder of one disk vector index, below <see cref="IndexEngine_VectorIndexFolderKey"/>.
    /// Same naming rule as <see cref="IndexEngine_LuceneIndexFolderKey"/> and for the same reasons.</summary>
    public static string IndexEngine_VectorIndexIndexFolderKey(string indexId) => indexId.ToLowerInvariant();

    /// <summary>
    /// The sibling key to write to before atomically replacing <paramref name="fileKey"/>, so a
    /// crash mid-write cannot leave a half-written file in place of the real one. A binary extension
    /// is replaced rather than appended to, keeping the temp file out of ".bin" search patterns.
    /// </summary>
    public static string[] TempFileKey(string[] fileKey) => [.. fileKey[..^1], TempFileName(fileKey.FileName())];
    /// <summary>Same rule as <see cref="TempFileKey"/> for a single file name or a full local path
    /// (the index engines write real files on the local disk).</summary>
    public static string TempFileName(string fileNameOrPath)
        => fileNameOrPath.EndsWith(binaryExtension) ? fileNameOrPath[..^binaryExtension.Length] + tempExtension : fileNameOrPath + tempExtension;

    #endregion

    #region STATIC helpers:

    /// <summary>The description of a file, from the joined form of its key as listings carry it
    /// (<see cref="FileMeta.Key"/>); folder browsers may pass a bare file name.</summary>
    public static string FileTypeDescription(string fileKey) {
        var key = fileKey.SplitKey();
        if (key.Length == 0) return "-";
        if (key.MatchesPattern(walFilePattern)) return "Primary database file";
        if (key.MatchesPattern(walSecondaryFilePattern)) return "Secondary databasefile";
        if (key.MatchesPattern(walFileBackupPatternKeepForever)) return "Backup [Never expiring]";
        if (key.MatchesPattern(walFileBackupPattern)) return "Backup";
        if (key.MatchesPattern(aiCacheFilePattern) || key.MatchesPattern(aiCacheNativeFilePattern)) return "AI Cache";
        if (key.MatchesPattern([indexStoreFolderPattern, "ai.cache.bin*"])) return "AI Temp";
        if (key.MatchesPattern(criticalErrorLogFilePattern)) return "Critical error log";
        if (key.MatchesPattern(mapperDllFilePattern)) return "Mapper DLL";
        if (key.MatchesPattern(fileStorePattern)) return "Filestore";
        if (key.MatchesPattern(stateFilePattern) || key.MatchesPattern(stateFileLegacyPattern)) return "State";
        if (key.MatchesPattern([indexStoreFolderPattern])) return "Index Store";
        if (key.MatchesPattern(queueFileKeyPattern)) return "Task queue";
        if (key.MatchesPattern(loggerAllFilePattern)) return "Log file";
        if (key.MatchesPattern(indexFilePattern)) return "Index";
        // index engine files: unprefixed, inside their engine folder (see the region above); matched
        // on the last segment so both bare names and folder qualified keys get a description
        var name = key.FileName();
        if (name == indexEngineNativeKvFile) return "Native index engine";
        if (name == indexEngineFacetSetsFile) return "Facet cache";
        if (name == indexEngineSqliteFile) return "Sqlite index engine";
        if (name == indexEngineLuceneWalIdFile) return "Lucene index engine log file id";
        if (name == "ai.cache.bin" || name == "native.ai.cache.bin") return "AI Cache";
        return "-";
    }

    /// <summary>The description of a folder, from its relative path in the joined form.</summary>
    /// <summary>What the folder at this relative path holds, in a few words, for listings. The
    /// primary data folders are named as such here as well; <see cref="IsPrimaryDataFolder"/> is
    /// what a caller should test to treat them differently.</summary>
    internal static string FolderTypeDescription(string relpath) {
        var key = relpath.SplitKey();
        if (key.Length == 0) return "-";
        // a primary data folder only carries the description at its own level: below it the names
        // are the store's own (hash folders in the file store), not something to describe
        if (key.Length > 1 && IsPrimaryDataFolder(relpath)) return "-";
        return key.FileName() switch {
            DataFolderName => "Database log files",
            StateFolderName => "State cache",
            BackupFolderName => "Backups",
            ConvertedFolderName => "Converted file cache",
            multiFileStoreFolderPattern => "File store",
            LogFolderName => "Logs",
            var s when s.MatchesWildcard(indexStoreFolderPattern) => "Indexes",
            indexEngineNativeKvFolder => "Native index engine",
            indexEngineSqliteFolder => "Sqlite index engine",
            indexEngineLuceneFolder => "Lucene index engine",
            _ => "-",
        };
    }

    // leaving ample room for folder paths and extensions; a numbered index state file name (guid,
    // culture, sub key and an 8 digit number) can reach ~70 characters
    public const int MaxFileNameLength = 100;
    static HashSet<char> _legalFileKeyCharacters = "abcdefghijklmnopqrstuvwxyz0123456789()-–_. ".ToHashSet();
    public static bool IsFileKeyValid(string fileKey) {
        if (string.IsNullOrEmpty(fileKey)) return false;
        if (fileKey.Length > MaxFileNameLength)
            return false;
        foreach (var c in fileKey.ToLower())
            if (!_legalFileKeyCharacters.Contains(c))
                return false;
        return true;
    }
    public static void ValidateFileKeyPath(string[] path) {
        foreach (var c in path) {
            ValidateFileKeyString(c);
        }
    }
    public static string FilterLegalCharInFileKey(string? fileKey) {
        if (string.IsNullOrWhiteSpace(fileKey)) return "unnamed";
        var sb = new System.Text.StringBuilder();
        foreach (var c in fileKey.ToLower()) {
            if (_legalFileKeyCharacters.Contains(c)) sb.Append(c);
        }
        fileKey = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(fileKey)) return "unnamed";
        return fileKey;
    }

    public static void ValidateFileKeyString(string fileKey) {
        // a segment must be a valid file name on its own, so a key can never escape the storage root
        if (string.IsNullOrEmpty(fileKey)) throwInvalidFileKey();
        if (fileKey == "." || fileKey == ".." || !IsFileKeyValid(fileKey)) throwInvalidFileKey();
    }
    static void throwInvalidFileKey() {
        throw new ArgumentException("Invalid file key. Name can only contain lowercase English letters, numbers, dash, space and underscores and have max length of " + MaxFileNameLength + " characters per path segment.");
    }
    static HashSet<char> _legalFilePrefixCharacters = "abcdefghijklmnopqrstuvwxyz0123456789".ToHashSet();
    public static bool IsFilePrefixValid(string prefix, [MaybeNullWhen(true)] out string? reason) {
        reason = null;
        if (string.IsNullOrEmpty(prefix)) return true;
        foreach (var word in storeNames) {
            if (prefix.Contains(word, StringComparison.OrdinalIgnoreCase)) {
                reason = $"Prefix cannot contain reserved word '{word}'.";
                return false;
            }
        }
        if (prefix.Length > 60) {
            reason = "Prefix is too long.";
            return false;
        }
        foreach (var c in prefix.ToLower()) {
            if (!_legalFilePrefixCharacters.Contains(c)) {
                reason = "Prefix can only contain lowercase letters and numbers. The following character is not allowed: " + c;
                return false;
            }
        }
        return true;
    }
    static void ValidateFilePrefixString(string prefix) {
        if (!IsFilePrefixValid(prefix, out var reason)) throw new ArgumentException("Invalid file prefix. " + reason);
    }

    #endregion

}
