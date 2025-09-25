using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.IO;
public class FileKeyUtility {

    string _prefix = "";
    static HashSet<string> reserved = new() { "db", "files", "index", "ai", "log", "mapper", "ai", "queue" }; // starting with these are reserved
    string walFilePattern => _prefix + "db.*.bin";
    string walFileBackupPattern => _prefix + "db.*.bkup";
    string walFileBackupPatternKeepForever => _prefix + "db.bkup.keep.*.bkup";

    string fileStorePattern => _prefix + "files.*.bin";
    string fileStoreBackupPattern => _prefix + "files.*.bkup";
    string fileStoreBackupPatternKeepForever => _prefix + "files.bkup.keep.*.bkup";

    string dateTimeTemplate => "yyyy-MM-dd-HH-mm-ss";
    string dateOnlyTemplate => "yyyy-MM-dd";
    string stateFilePattern => _prefix + "index.state.bin";

    string aiCacheFilePattern => _prefix + "ai.cache.bin";
    string indexStoreFolderPattern => _prefix + "indexes";

    string mapperDllFilePattern => _prefix + "mapper.*.dll";

    string loggerAllFilePattern => _prefix + "log.*";
    string loggerPrefix => _prefix + "log";
    string loggerFilePartDelim => ".";
    string loggerDatePartsDelim => "-";
    string loggerStatisticsSuffix => "statistics";
    string loggerBinaryExt => ".bin";
    string loggerTextExt => ".txt";
    string loggerBkUpExt => ".bkup";

    string queueFileKey => _prefix + "queue";
    string queueFileKeyPattern => _prefix + "queue.*";


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

    public string StateFileKey => stateFilePattern;
    public string AiCacheFileKey => aiCacheFilePattern;
    public string IndexStoreFolderKey => indexStoreFolderPattern;

    public string[] GetAllFileKeys(IIOProvider io) => [.. io.Search(_prefix + "*").Order()];
    public FileMeta[] GetAllFiles(IIOProvider io) => [.. io.SearchMeta(_prefix + "*")];

    public DateOnly SystemLog_GetFileDateTimeFromFileKey(string fileKey) {
        var parts = fileKey.Split('.');
        var dtSection = parts[^2];
        return DateOnly.ParseExact(dtSection, dateOnlyTemplate, System.Globalization.CultureInfo.InvariantCulture);
    }

    public string WAL_GetFileKey(int n) => walFilePattern.Replace("*", n.ToString("00000000"));
    public string[] WAL_GetAllFileKeys(IIOProvider io) => io.Search(walFilePattern).Order().ToArray();
    public string WAL_GetLatestFileKey(IIOProvider io) => WAL_GetAllFileKeys(io).LastOrDefault() ?? WAL_GetFileKey(1);
    public string WAL_NextFileKey(IIOProvider io) {
        var parts = WAL_GetLatestFileKey(io).Split('.');
        var numberSection = parts[^2];
        return WAL_GetFileKey(int.Parse(numberSection) + 1);
    }
    public DateTime WAL_GetBackUpDateTimeFromFileKey(string fileKey) {
        var parts = fileKey.Split('.');
        var dtSection = parts[^2];
        var dt = DateTime.ParseExact(dtSection, dateTimeTemplate, System.Globalization.CultureInfo.InvariantCulture);
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }
    public string[] WAL_GetAllBackUpFileKeys(IIOProvider io) => [.. io.Search(walFileBackupPattern).Order()];
    public string WAL_GetFileKeyForBackup(DateTime dt, bool keepForever) => (keepForever ? walFileBackupPatternKeepForever : walFileBackupPattern).Replace("*", dt.ToString(dateTimeTemplate));
    public bool WAL_KeepForever(string fileKey) => fileKey.MatchesWildcard(walFileBackupPatternKeepForever);


    public string FileStore_GetFileKey(int n) => fileStorePattern.Replace("*", n.ToString("00000000"));
    public string[] FileStore_GetAllFileKeys(IIOProvider io) => io.Search(fileStorePattern).Order().ToArray();
    public string FileStore_GetLatestFileKey(IIOProvider io) => FileStore_GetAllFileKeys(io).LastOrDefault() ?? FileStore_GetFileKey(1);
    public string FileStore_NextFileKey(IIOProvider io) {
        var parts = FileStore_GetLatestFileKey(io).Split('.');
        var numberSection = parts[^2];
        return FileStore_GetFileKey(int.Parse(numberSection) + 1);
    }
    public DateTime FileStore_GetBackUpDateTimeFromFileKey(string fileKey) {
        var parts = fileKey.Split('.');
        var dtSection = parts[^2];
        var dt = DateTime.ParseExact(dtSection, dateTimeTemplate, System.Globalization.CultureInfo.InvariantCulture);
        return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
    }
    public string[] FileStore_GetAllBackUpFileKeys(IIOProvider io) => [.. io.Search(fileStoreBackupPattern).Order()];
    public string FileStore_GetFileKeyForBackup(DateTime dt, bool keepForever) => (keepForever ? fileStoreBackupPatternKeepForever : fileStoreBackupPattern).Replace("*", dt.ToString(dateTimeTemplate));
    public bool FileStore_KeepForever(string fileKey) => fileKey.MatchesWildcard(fileStoreBackupPatternKeepForever);

    public string MapperDll_GetFileKey(ulong hash) => mapperDllFilePattern.Replace("*", hash.ToString());
    public string[] MapperDll_GetAllFileKeys(IIOProvider io) => io.Search(mapperDllFilePattern).Order().ToArray();

    public string Logger_GetFilePrefix() => loggerPrefix;
    public string Logger_GetFilePartDelimiter() => loggerFilePartDelim;
    public string Logger_GetFileDatePartsDelimiter() => loggerDatePartsDelim;
    public string Logger_GetStatisticsSuffix() => loggerStatisticsSuffix;
    public string Logger_GetBinaryExtension() => loggerBinaryExt;
    public string Logger_GetTextExtension() => loggerTextExt;
    public string Logger_GetStatistics(string loggerKey) => loggerPrefix + loggerFilePartDelim + loggerKey + loggerFilePartDelim + loggerStatisticsSuffix + loggerBinaryExt;
    public string Logger_GetStatisticsBackUp(string loggerKey) => Logger_GetStatistics(loggerKey) + loggerBkUpExt;


    public string Queue_GetFileKey(string ext) => queueFileKey + "." + ext;


    #region STATIC helpers:

    static FileKeyUtility _anyPrefix = new(null) { _prefix = "*" }; // done so description can be static...
    public static bool IsTemporary(string fileKey) {
        return !fileKey.MatchesWildcard(_anyPrefix.walFilePattern);
    }
    public static string FileTypeDescription(string fileKey) {
        ValidateFileKeyString(fileKey);
        if (fileKey.MatchesWildcard(_anyPrefix.walFilePattern)) return "Database";
        if (fileKey.MatchesWildcard(_anyPrefix.walFileBackupPattern)) return "Backup";
        if (fileKey.MatchesWildcard(_anyPrefix.walFileBackupPatternKeepForever)) return "Backup Protected";
        if (fileKey.MatchesWildcard(_anyPrefix.aiCacheFilePattern)) return "AI Cache";
        if (fileKey.MatchesWildcard(_anyPrefix.mapperDllFilePattern)) return "Mapper DLL";
        if (fileKey.MatchesWildcard(_anyPrefix.fileStorePattern)) return "Filestore";
        if (fileKey.MatchesWildcard(_anyPrefix.stateFilePattern)) return "Index State";
        if (fileKey.MatchesWildcard(_anyPrefix.indexStoreFolderPattern)) return "Index Store";
        if (fileKey.MatchesWildcard(_anyPrefix.queueFileKeyPattern)) return "Task queue";
        if (fileKey.MatchesWildcard(_anyPrefix.loggerAllFilePattern)) return "Log file";
        return "-";
    }
    static HashSet<char> _legalFileKeyCharacters = "abcdefghijklmnopqrstuvwxyz0123456789()-–_. ".ToHashSet();
    public static bool IsFileKeyValid(string fileKey) {
        if (string.IsNullOrEmpty(fileKey)) return false;
        if (fileKey.Length > 100)
            return false;
        foreach (var c in fileKey.ToLower())
            if (!_legalFileKeyCharacters.Contains(c))
                return false;
        return true;
    }
    public static void ValidateFileKeyString(string fileKey) {
        if (!IsFileKeyValid(fileKey)) throw new ArgumentException("Invalid file key. Name can only contain lowercase English letters, numbers, dash, space and underscores and have max length of " + 100 + " characters.");
    }
    static HashSet<char> _legalFilePrefixCharacters = "abcdefghijklmnopqrstuvwxyz0123456789".ToHashSet();
    public static bool IsFilePrefixValid(string prefix, [MaybeNullWhen(true)] out string? reason) {
        reason = null;
        if (string.IsNullOrEmpty(prefix)) return true;
        foreach (var word in reserved) {
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
    public static void ValidateFilePrefixString(string prefix) {
        if (!IsFilePrefixValid(prefix, out var reason)) throw new ArgumentException("Invalid file prefix. " + reason);
    }
    #endregion

}
