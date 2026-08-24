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
///     data/  = the database log files (primary and secondary)
///     state/ = everything rebuildable from the log: state snapshot, memory index states, mapper dll, persisted queue
///     bkup/  = backup files
///     log/   = the system logger files and the critical error log
/// The folder names are plain (no prefix); the instance prefix stays on the file name inside them,
/// so several instances can still share one storage location. The prefixed indexes folder and the
/// multi file store folder are unchanged.
///
/// Wildcard patterns are file keys with wildcards in their segments, matched segment by segment
/// (<see cref="FileKeyExtensions.MatchesPattern"/>). Listings (<see cref="FileMeta.Key"/>) carry
/// the joined ('/'-separated) form of a key.
/// </summary>
public class FileKeyUtility {

    /// <summary>Folder of the database log files (primary and secondary).</summary>
    public const string DataFolderName = "data";
    /// <summary>Folder of everything rebuildable from the log: state snapshot, memory index states, mapper dll, persisted queue.</summary>
    public const string StateFolderName = "state";
    /// <summary>Folder of the backup files.</summary>
    public const string BackupFolderName = "bkup";
    /// <summary>Folder of the system logger files and the critical error log.</summary>
    public const string LogFolderName = "log";
    /// <summary>The well known folders below the storage root. Disk providers include these in file listings.</summary>
    public static readonly string[] SystemFolderNames = [DataFolderName, StateFolderName, BackupFolderName, LogFolderName];

    string _prefix = "";
    static HashSet<string> storeNames = new() { "db", "files", "index", "ai", "log", "mapper", "ai", "queue" }; // starting with these are reserved
    // patterns are file keys with wildcards in their segments, matched segment by segment
    string[] walSecondaryFilePattern => [DataFolderName, _prefix + "db.log"];
    string[] walFilePattern => [DataFolderName, _prefix + "db.*.bin"];
    string[] walFileBackupPattern => [BackupFolderName, _prefix + "db.*.bkup"];
    string[] walFileBackupPatternKeepForever => [BackupFolderName, _prefix + "db.bkup.keep.*.bkup"];

    string multiFileStoreFolderPattern => _prefix + "files";
    string[] fileStorePattern => [_prefix + "files.*.bin"];
    string[] fileStoreBackupPattern => [BackupFolderName, _prefix + "files.*.bkup"];
    string[] fileStoreBackupPatternKeepForever => [BackupFolderName, _prefix + "files.bkup.keep.*.bkup"];

    string dateTimeTemplate => "yyyy-MM-dd-HH-mm-ss";
    string dateOnlyTemplate => "yyyy-MM-dd";
    string[] stateFilePattern => [StateFolderName, _prefix + "state.bin"];
    string[] indexFilePattern => [StateFolderName, _prefix + "index.*.bin"];

    // the ai cache lives in the indexes folder; that folder is already prefixed, so like the index
    // engine files below it the file name itself carries no prefix
    string[] aiCacheFilePattern => [indexStoreFolderPattern, "ai.cache.bin"];
    string[] aiCacheNativeFilePattern => [indexStoreFolderPattern, "native.ai.cache.bin"];
    string indexStoreFolderPattern => _prefix + "indexes";

    string[] mapperDllFilePattern => [StateFolderName, _prefix + "mapper.*.dll"];

    string[] loggerAllFilePattern => [LogFolderName, _prefix + "log.*"];
    string loggerNamePrefix => _prefix + "log"; // the file name part; logger files live in the log folder
    string loggerFilePartDelim => ".";
    string loggerDatePartsDelim => "-"; // cannot be changed!
    string loggerStatisticsSuffix => "statistics";
    string loggerBinaryExt => ".bin";
    string loggerTextExt => ".txt";
    string loggerBkUpExt => ".bkup";

    string[] criticalErrorLogFilePattern => [LogFolderName, _prefix + "critical.error.txt"];

    string queueFileName => _prefix + "queue";
    string[] queueFileKeyPattern => [StateFolderName, _prefix + "queue.*"];

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

    public FileKeyUtility(string? prefix) {
        // filter prefix for letters, numbers, and underscores:
        if (prefix != null) {
            ValidateFilePrefixString(prefix);
            foreach (var c in prefix) {
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '.')
                    throw new ArgumentException("Prefix can only contain letters, numbers, and underscores.");
            }
        }
        _prefix = string.IsNullOrEmpty(prefix) ? "" : prefix.Trim();
        if (_prefix.Length > 0 && !_prefix.EndsWith(".")) _prefix += ".";
    }

    public string MultiFileStoreFolderKey => multiFileStoreFolderPattern;
    public string[] StateFileKey => stateFilePattern;
    public string[]? GetAiCacheFileKey(AIProviderCacheType? cacheProvider) {
        if (cacheProvider == null) return null;
        return cacheProvider.Value switch {
            AIProviderCacheType.None => null,
            AIProviderCacheType.Native => aiCacheNativeFilePattern,
            AIProviderCacheType.Memory => null,
            AIProviderCacheType.Sqlite => aiCacheFilePattern,
            _ => throw new NotImplementedException(),
        };
    }
    public string IndexStoreFolderKey => indexStoreFolderPattern;

    public string[] CriticalErrorLogFileKey => criticalErrorLogFilePattern;

    public string[][] GetAllFileKeys(IIOProvider io) => [.. io.GetFiles().Select(f => f.KeyOf()).Where(isInstanceFileKey).OrderBy(k => k.AsKeyString())];
    public FileMeta[] GetAllFiles(IIOProvider io) => [.. io.GetFiles().Where(f => isInstanceFileKey(f.KeyOf())).OrderBy(f => f.Key)];
    /// <summary>
    /// Whether the key belongs to this instance: a root file or a system folder file whose name
    /// carries the instance prefix. Keys in other folders (the prefixed indexes folder, the multi
    /// file store folder) are matched on their first segment, as before the folder layout.
    /// </summary>
    bool isInstanceFileKey(string[] key) {
        if (key.Length > 1 && SystemFolderNames.Contains(key[0])) return key[1].MatchesWildcard(_prefix + "*");
        return key[0].MatchesWildcard(_prefix + "*");
    }

    public DateOnly SystemLog_GetFileDateTimeFromFileKey(string[] fileKey) {
        var parts = fileKey.FileName().Split('.');
        var dtSection = parts[^2];
        return DateOnly.ParseExact(dtSection, dateOnlyTemplate, System.Globalization.CultureInfo.InvariantCulture);
    }

    public string[] Index_GetFileKey(string indexId) {
        return fill(indexFilePattern, indexId);
    }
    public string[][] Index_GetAll(IIOProvider io) {
        return [.. io.Search(indexFilePattern)];
    }


    public string[] WAL_GetFileKey(int n) => fill(walFilePattern, n.ToString("00000000"));
    public string[][] WAL_GetAllFileKeys(IIOProvider io) => [.. io.Search(walFilePattern)];
    public string[] WAL_GetSecondaryFileKey() => walSecondaryFilePattern;
    public string[] WAL_GetLatestFileKey(IIOProvider io) => WAL_GetAllFileKeys(io).LastOrDefault() ?? WAL_GetFileKey(1);
    public string[] WAL_NextFileKey(IIOProvider io) {
        var parts = WAL_GetLatestFileKey(io).FileName().Split('.');
        var numberSection = parts[^2];
        return WAL_GetFileKey(int.Parse(numberSection) + 1);
    }
    public DateTime WAL_GetBackUpDateTimeFromFileKey(string[] fileKey) {
        var parts = fileKey.FileName().Split('.');
        var dtSection = parts[^2];
        var dt = DateTime.ParseExact(dtSection, dateTimeTemplate, System.Globalization.CultureInfo.InvariantCulture);
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }
    public string[][] WAL_GetAllBackUpFileKeys(IIOProvider io) => [.. io.Search(walFileBackupPattern)];
    public string[] WAL_GetFileKeyForBackup(DateTime dt, bool keepForever)
        => fill(keepForever ? walFileBackupPatternKeepForever : walFileBackupPattern, dt.ToString(dateTimeTemplate));
    public bool WAL_KeepForever(string[] fileKey) => fileKey.MatchesPattern(walFileBackupPatternKeepForever);

    public string[] FileStore_GetFileKey(int n) => fill(fileStorePattern, n.ToString("00000000"));
    public string[][] FileStore_GetAllFileKeys(IIOProvider io) => [.. io.Search(fileStorePattern)];
    public string[] FileStore_GetLatestFileKey(IIOProvider io) => FileStore_GetAllFileKeys(io).LastOrDefault() ?? FileStore_GetFileKey(1);
    public string[] FileStore_NextFileKey(IIOProvider io) {
        var parts = FileStore_GetLatestFileKey(io).FileName().Split('.');
        var numberSection = parts[^2];
        return FileStore_GetFileKey(int.Parse(numberSection) + 1);
    }
    public DateTime FileStore_GetBackUpDateTimeFromFileKey(string[] fileKey) {
        var parts = fileKey.FileName().Split('.');
        var dtSection = parts[^2];
        var dt = DateTime.ParseExact(dtSection, dateTimeTemplate, System.Globalization.CultureInfo.InvariantCulture);
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }
    public string[][] FileStore_GetAllBackUpFileKeys(IIOProvider io) => [.. io.Search(fileStoreBackupPattern)];
    public string[] FileStore_GetFileKeyForBackup(DateTime dt, bool keepForever)
        => fill(keepForever ? fileStoreBackupPatternKeepForever : fileStoreBackupPattern, dt.ToString(dateTimeTemplate));
    public bool FileStore_KeepForever(string[] fileKey) => fileKey.MatchesPattern(fileStoreBackupPatternKeepForever);

    public string[] MapperDll_GetFileKey(ulong hash) => fill(mapperDllFilePattern, hash.ToString());
    public string[][] MapperDll_GetAllFileKeys(IIOProvider io) => [.. io.Search(mapperDllFilePattern)];

    public string[] Logger_GetStatistics(string loggerKey) => [LogFolderName, loggerNamePrefix + loggerFilePartDelim + loggerKey + loggerFilePartDelim + loggerStatisticsSuffix + loggerBinaryExt];
    public string[] Logger_GetStatisticsBackUp(string loggerKey) {
        var key = Logger_GetStatistics(loggerKey);
        return [.. key[..^1], key.FileName() + loggerBkUpExt];
    }
    /// <summary>The file name prefix (inside the log folder) of one log's files for the given interval.</summary>
    public string Logger_NamePrefix(string logName, FileInterval fileInterval) => loggerNamePrefix + loggerFilePartDelim + logName + loggerFilePartDelim + fileInterval.ToString().ToLower() + loggerFilePartDelim;
    public string[] Logger_FileNameBin(string logName, FileInterval fileInterval, DateTime floored) {
        return logger_FileName(logName, fileInterval, floored, loggerBinaryExt);
    }
    public string[] Logger_FileNameTxt(string logName, FileInterval fileInterval, DateTime floored) {
        return logger_FileName(logName, fileInterval, floored, loggerTextExt);
    }
    public List<DateTime> Logger_FileDatesBin(IIOProvider io, string logName, FileInterval fileInterval) => getLogFileDates(io, logName, fileInterval, loggerBinaryExt);
    public List<DateTime> Logger_FileDatesTxt(IIOProvider io, string logName, FileInterval fileInterval) => getLogFileDates(io, logName, fileInterval, loggerTextExt);
    string[] logger_FileName(string logName, FileInterval fileInterval, DateTime floored, string fileExt) {
        var name = (Logger_NamePrefix(logName, fileInterval) + fileInterval switch {
            FileInterval.Minute => floored.ToString("yyyy-MM-dd-HH-mm"),
            FileInterval.Hour => floored.ToString("yyyy-MM-dd-HH"),
            FileInterval.Day => floored.ToString("yyyy-MM-dd"),
            FileInterval.Month => floored.ToString("yyyy-MM"),
            _ => throw new NotImplementedException(),
        }).ToLower() + fileExt;
        return [LogFolderName, name];
    }
    DateTime logger_ParseFileName(string fileName, string logName, FileInterval fileInterval, string fileExt) {
        var datePart = fileName[Logger_NamePrefix(logName, fileInterval).Length..];
        datePart = datePart.Substring(0, datePart.Length - fileExt.Length);
        var p = datePart.Split(loggerDatePartsDelim).Select(p => int.Parse(p)).ToArray();
        return fileInterval switch {
            FileInterval.Minute => new DateTime(p[0], p[1], p[2], p[3], p[4], 0, DateTimeKind.Utc),
            FileInterval.Hour => new DateTime(p[0], p[1], p[2], p[3], 0, 0, DateTimeKind.Utc),
            FileInterval.Day => new DateTime(p[0], p[1], p[2], 0, 0, 0, DateTimeKind.Utc),
            FileInterval.Month => new DateTime(p[0], p[1], 1, 0, 0, 0, DateTimeKind.Utc),
            _ => throw new NotImplementedException(),
        };
    }
    List<DateTime> getLogFileDates(IIOProvider io, string logName, FileInterval fileInterval, string fileExt) {
        string[] pattern = [LogFolderName, Logger_NamePrefix(logName, fileInterval) + "*" + fileExt];
        return io.Search(pattern).Select(f => logger_ParseFileName(f.FileName(), logName, fileInterval, fileExt)).OrderBy(i => i).ToList();
    }

    public string[] Queue_GetFileKey(string ext) => [StateFolderName, queueFileName + "." + ext];

    // Before the folder layout every file lived in the storage root. The startup migration moves
    // those files into their folders using the helpers below.
    public string[][] WAL_GetLegacyRootFileKeys(IIOProvider io) => [.. io.Search([_prefix + "db.*.bin"])];
    public string[] WAL_GetLegacyRootSecondaryFileKey() => [_prefix + "db.log"];
    public static string[] MapLegacyRootFileKeyToDataFolder(string[] legacyRootFileKey) => [DataFolderName, .. legacyRootFileKey];
    /// <summary>Database and file store backups in the storage root; they now live in the bkup folder.</summary>
    public string[][] Legacy_GetRootBackupFileKeys(IIOProvider io)
        => [.. io.Search([_prefix + "db.*.bkup"]), .. io.Search([_prefix + "files.*.bkup"])];
    /// <summary>State snapshot, index states, mapper dlls and queue files in the storage root; they now live in the state folder.</summary>
    public string[][] Legacy_GetRootStateFileKeys(IIOProvider io)
        => [.. io.Search([_prefix + "state.bin"]), .. io.Search([_prefix + "index.*.bin"]), .. io.Search([_prefix + "mapper.*.dll"]), .. io.Search([_prefix + "queue.*"])];
    /// <summary>Logger files and the critical error log in the storage root; they now live in the log folder.</summary>
    public string[][] Legacy_GetRootLoggerFileKeys(IIOProvider io)
        => [.. io.Search([_prefix + "log.*"]), .. io.Search([_prefix + "critical.error.txt"])];
    /// <summary>
    /// The file name the ai cache had in the root of its local disk folder before it moved into the
    /// prefixed indexes folder (where the prefix left the file name), or null when the cache type
    /// has no file. The cache lives on the local disk outside the IO providers, so this is a plain
    /// file name for the server layer's migration.
    /// </summary>
    public string? GetLegacyRootAiCacheFileName(AIProviderCacheType? cacheProvider) {
        return cacheProvider switch {
            AIProviderCacheType.Native => "native." + _prefix + "ai.cache.bin",
            AIProviderCacheType.Sqlite => _prefix + "ai.cache.bin",
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

    static FileKeyUtility _anyPrefix = new(null) { _prefix = "*" }; // done so description can be static...

    /// <summary>The description of a file, from the joined form of its key as listings carry it
    /// (<see cref="FileMeta.Key"/>); folder browsers may pass a bare file name.</summary>
    public static string FileTypeDescription(string fileKey) {
        var key = fileKey.SplitKey();
        if (key.Length == 0) return "-";
        if (key.MatchesPattern(_anyPrefix.walFilePattern)) return "Primary database file";
        if (key.MatchesPattern(_anyPrefix.walSecondaryFilePattern)) return "Secondary databasefile";
        if (key.MatchesPattern(_anyPrefix.walFileBackupPatternKeepForever)) return "Backup [Never expiring]";
        if (key.MatchesPattern(_anyPrefix.walFileBackupPattern)) return "Backup";
        if (key.MatchesPattern(_anyPrefix.aiCacheFilePattern)) return "AI Cache";
        if (key.MatchesPattern(_anyPrefix.aiCacheNativeFilePattern)) return "AI Cache";
        if (key.MatchesPattern([_anyPrefix.indexStoreFolderPattern, "ai.cache.bin*"])) return "AI Temp";
        if (key.MatchesPattern(_anyPrefix.criticalErrorLogFilePattern)) return "Critical error log";
        if (key.MatchesPattern(_anyPrefix.mapperDllFilePattern)) return "Mapper DLL";
        if (key.MatchesPattern(_anyPrefix.fileStorePattern)) return "Filestore";
        if (key.MatchesPattern(_anyPrefix.stateFilePattern)) return "State";
        if (key.MatchesPattern([_anyPrefix.indexStoreFolderPattern])) return "Index Store";
        if (key.MatchesPattern(_anyPrefix.queueFileKeyPattern)) return "Task queue";
        if (key.MatchesPattern(_anyPrefix.loggerAllFilePattern)) return "Log file";
        if (key.MatchesPattern(_anyPrefix.indexFilePattern)) return "Index";
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
    internal static string FolderTypeDescription(string relpath) {
        var key = relpath.SplitKey();
        if (key.Length == 0) return "-";
        return key.FileName() switch {
            DataFolderName => "[Primary data files]",
            StateFolderName => "State cache",
            BackupFolderName => "Backups",
            "converted" => "Converted file cache",
            "files" => "[Primary file store]",
            LogFolderName => "Logs",
            var s when s.MatchesWildcard(_anyPrefix.indexStoreFolderPattern) => "Indexes",
            indexEngineNativeKvFolder => "Native index engine",
            indexEngineSqliteFolder => "Sqlite index engine",
            indexEngineLuceneFolder => "Lucene index engine",
            _ => "-",
        };
    }

    public const int MaxFileNameLength = 64; // leaving ample room for folder paths and extensions
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
