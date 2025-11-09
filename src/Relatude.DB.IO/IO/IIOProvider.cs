namespace Relatude.DB.IO;
public interface IIOProvider {
    IReadStream OpenRead(string fileKey, long position);
    IAppendStream OpenAppend(string fileKey);
    bool DoesNotExistOrIsEmpty(string fileKey);
    void DeleteIfItExists(string fileKey);
    FileMeta[] GetFiles();
    void ResetLocks();
    long GetFileSizeOrZeroIfUnknown(string file);
    bool CanRenameFile { get; }
    void RenameFile(string fileKey, string newFileKey);
}
public static class IIOProviderExtensions {
    public static List<string> Search(this IIOProvider io, string? wildcardPattern = null) {
        return io.GetFiles().Select(f => f.Key).FilterByWildcard(wildcardPattern).ToList();
    }
    public static List<FileMeta> SearchMeta(this IIOProvider io, string? wildcardPattern = null) {
        return io.GetFiles().Where(f => wildcardPattern != null && f.Key.MatchesWildcard(wildcardPattern)).OrderBy(f => f.Key).ToList();
    }
    public static string ReadString(this IIOProvider io, string fileKey, string? fallback = null) {
        if (io.GetFileSizeOrZeroIfUnknown(fileKey) == 0) return fallback ?? string.Empty;
        using var stream = io.OpenRead(fileKey, 0);
        return stream.ReadString();
    }
    public static void WriteString(this IIOProvider io, string fileKey, string content) {
        io.DeleteIfItExists(fileKey);
        using var stream = io.OpenAppend(fileKey);
        stream.WriteString(content);
    }
    public static byte[] ReadAllBytes(this IIOProvider io, string fileKey) {
        using var stream = io.OpenRead(fileKey, 0);
        return stream.Read((int)stream.Length);
    }
    public static void WriteAllBytes(this IIOProvider io, string fileKey, byte[] content) {
        io.DeleteIfItExists(fileKey);
        using var stream = io.OpenAppend(fileKey);
        stream.Append(content);
    }
    public static string ReadAllTextUTF8(this IIOProvider io, string fileKey) {
        using var stream = io.OpenRead(fileKey, 0);
        return stream.ReadUTF8StringNoLengthPrefix((int)stream.Length);
    }
    public static void WriteAllTextUTF8(this IIOProvider io, string fileKey, string content) {
        io.DeleteIfItExists(fileKey);
        using var stream = io.OpenAppend(fileKey);
        stream.WriteUTF8StringNoLengthPrefix(content);
    }
    public static bool DoesNotExistsOrIsEmpty(this IIOProvider io, string fileKey) {
        return io.GetFileSizeOrZeroIfUnknown(fileKey) == 0;
    }
    public static bool ExistsAndIsNotEmpty(this IIOProvider io, string fileKey) {
        return io.GetFileSizeOrZeroIfUnknown(fileKey) > 0;
    }
    public static void CopyIfItExistsAndOverwrite(this IIOProvider io, string fileKeySource, string fileKeyDest) {
        if (io.DoesNotExistOrIsEmpty(fileKeySource)) return;
        io.DeleteIfItExists(fileKeyDest);
        using var readStream = io.OpenRead(fileKeySource, 0);
        using var writeStream = io.OpenAppend(fileKeyDest);
        if (readStream.Length > 1024 * 1024 * 100) { // max 100 mb this method
            throw new Exception("File too big to copy. ");
        }
        writeStream.Append(readStream.Read((int)readStream.Length));
    }
    public static string EnsureDirectorySeparatorChar(this string path1) {
        var delimiter = Path.DirectorySeparatorChar;
        var otherDelimiter = delimiter == '/' ? '\\' : '/';
        return path1.Replace(otherDelimiter, delimiter);
    }
    public static bool VerifyPathIsUnderThis(this string pathToVerify, string rootPath) {
        try {
            var fullRoot = Path.GetFullPath(rootPath);
            var fullPathToVerify = Path.GetFullPath(pathToVerify);
            return fullPathToVerify.StartsWith(fullRoot, StringComparison.InvariantCultureIgnoreCase);
        } catch { return false; }
    }
    public static string SuperPathCombine(this string path1, string? path2) {
        if (path2 == null) return path1;
        var delimiter = Path.DirectorySeparatorChar;
        var otherDelimiter = delimiter == '/' ? '\\' : '/';
        path1 = path1.Replace(otherDelimiter, delimiter);
        path2 = path2.Replace(otherDelimiter, delimiter);
        while (path1.EndsWith(delimiter)) path1 = path1.Substring(0, path1.Length - 1);
        while (path2.StartsWith(delimiter)) path2 = path2.Substring(1);
        if (path1.Length == 0) return path2;
        if (path2.Length == 0) return path1;
        return path1 + delimiter + path2;
    }
    public static void CopyFile(this IIOProvider io, string fromFileName, string toIoFileName) {
        if (!io.DoesNotExistsOrIsEmpty(toIoFileName)) throw new Exception("File already exists");
        using var fromReader = io.OpenRead(fromFileName, 0);
        using var toWriter = io.OpenAppend(toIoFileName);
        using var readStream = new ReadStreamWrapper(fromReader);
        using var writeStream = new WriteStreamWrapper(toWriter);
        readStream.CopyTo(writeStream);
    }
    public static void CopyFile(this IIOProvider fromIo, IIOProvider toIo, string fromFileName, string toIoFileName) {
        if (!toIo.DoesNotExistsOrIsEmpty(toIoFileName)) throw new Exception("File already exists");
        using var fromReader = fromIo.OpenRead(fromFileName, 0);
        using var toWriter = toIo.OpenAppend(toIoFileName);
        using var readStream = new ReadStreamWrapper(fromReader);
        using var writeStream = new WriteStreamWrapper(toWriter);
        readStream.CopyTo(writeStream);
    }
    public static void CopyFile(this IIOProvider fromIo, IIOProvider toIo, string fromFileName, string toIoFileName, Action<int> progress) {
        if (!toIo.DoesNotExistsOrIsEmpty(toIoFileName)) throw new Exception("File already exists");
        using var fromReader = fromIo.OpenRead(fromFileName, 0);
        using var toWriter = toIo.OpenAppend(toIoFileName);
        using var readStream = new ReadStreamWrapper(fromReader);
        using var writeStream = new WriteStreamWrapper(toWriter);
        var buffer = new byte[1024 * 1024]; // 1 MB buffer
        long totalBytesCopied = 0;
        int bytesRead;
        while ((bytesRead = readStream.Read(buffer, 0, buffer.Length)) > 0) {
            writeStream.Write(buffer, 0, bytesRead);
            totalBytesCopied += bytesRead;
            var progressPercent = (int)((totalBytesCopied * 100) / fromReader.Length);
            progress(progressPercent);
        }
    }
}