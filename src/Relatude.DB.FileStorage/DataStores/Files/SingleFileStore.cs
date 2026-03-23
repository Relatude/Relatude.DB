using Relatude.DB.IO;
using Relatude.DB.Common;
using System.Text;
using System.Security.Cryptography;
namespace Relatude.DB.DataStores.Files;

public class SingleFileStore : IDisposable, IFileStore {
    object _fileLock = new();
    IAppendStream? _file;
    readonly IIOProvider _ioProvider;
    readonly string _fileKey;
    AsyncReaderWriterLock _asyncLock = new(); // multiple readers, single writer
    static readonly byte[] _fileEndMarker = Encoding.UTF8.GetBytes("JD-244Dm=" + "D411+-*" + "s5jk" + "mDsdchy").Reverse().ToArray();
    const long byteLengthOfMD5Checksum = 32;
    public SingleFileStore(Guid id, IIOProvider ioProvider, string fileKey) {
        Id = id;
        _ioProvider = ioProvider;
        _fileKey = fileKey;
    }
    IAppendStream file {
        get {
            lock (_fileLock) {
                if (_file == null) _file = _ioProvider.OpenAppend(_fileKey);
                return _file;
            }
        }
    }
    public Guid Id { get; }
    async Task<SingleStorageFileMeta> insert(Func<byte[], Task<byte[]>> readAsync, long length, string originalFileName) {
        var offset = file.Length;
        var bufferSize = 5 * 1024 * 1024; // 5MB buffer 
        bufferSize = length < bufferSize ? (int)length : bufferSize;
        var buffer = new byte[bufferSize];
        long totalBytesRead = 0;
        byte[] checksum = [];
        var id = Guid.NewGuid();
        file.WriteGuid(id);
        file.WriteVerifiedLong(length);
        file.WriteString(originalFileName);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        while (totalBytesRead < length) {
            buffer = await readAsync(buffer);  // buffer returned must be exact size, but buffer can be reused
            hash.AppendData(buffer, 0, buffer.Length);
            await file.AppendAsyncNoChecksumOrLock(buffer, buffer.Length);
            //file.Append(buffer);
            totalBytesRead += buffer.Length;
        }
        checksum = hash.GetHashAndReset();
        if (totalBytesRead != length) throw new Exception("Length mismatch");
        file.WriteByteArray(checksum);
        file.WriteByteArray(_fileEndMarker);
        return new SingleStorageFileMeta(id, offset, length, checksum, originalFileName);
    }
    async Task<SingleStorageFileMeta> extract(SingleStorageFileMeta meta, Func<byte[], Task> recieveAsync) {
        long pos = meta.Offset;
        long length;
        Guid id;
        lock (_fileLock) id = file.GetGuid(ref pos);
        if (id != meta.Id) throw new Exception("File corruption, id mismatch. ");
        lock (_fileLock) length = file.GetVerifiedLong(ref pos);
        if (length != meta.Length) throw new Exception("File corruption, length mismatch. ");
        long bytesLeft = length;
        var fileName = "";
        lock (_fileLock) fileName = file.GetString(ref pos); // allow filename to be different from original filename, renaming is allowed
        var remaining = length + byteLengthOfMD5Checksum + _fileEndMarker.Length;
        if (pos + remaining > file.Length) throw new Exception("File corruption, file too short. ");
        var count = (int)Math.Min(1024 * 1024, length); // 1MB
        var buffer = new byte[count];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        while (bytesLeft > 0) {
            count = (int)Math.Min(buffer.Length, bytesLeft);
            if (count < buffer.Length) buffer = new byte[count]; // new buffer for last read, needs to be exact size
            lock (_fileLock) file.Get(pos, count, buffer);
            await recieveAsync(buffer);
            hash.AppendData(buffer, 0, buffer.Length);
            pos += count;
            bytesLeft -= count;
        }
        byte[] calculatedChecksum = hash.GetHashAndReset();
        byte[] storedChecksum;
        lock (_fileLock) storedChecksum = file.GetByteArray(ref pos);
        byte[] marker;
        lock (_fileLock) marker = file.GetByteArray(ref pos);
        if (!marker.SequenceEqual(_fileEndMarker)) throw new Exception("File corruption, end marker mismatch. ");
        if (!calculatedChecksum.SequenceEqual(storedChecksum)) throw new Exception("File corruption, internal checksum mismatch. ");
        if (!meta.Checksum.SequenceEqual(calculatedChecksum)) throw new Exception("File corruption, meta checksum mismatch. ");
        return new SingleStorageFileMeta(id, meta.Offset, length, calculatedChecksum, fileName);
    }
    async Task<SingleStorageFileMeta> insertWithWriteLock(Func<byte[], Task<byte[]>> readAsync, long length, string originalFileName) {
        await _asyncLock.AcquireWriterLock();
        try {
            return await insert(readAsync, length, originalFileName);
        } finally {
            _asyncLock.ReleaseWriterLock();
        }
    }
    async Task<SingleStorageFileMeta> extractWithReadLock(SingleStorageFileMeta meta, Func<byte[], Task> recieveAsync) {
        await _asyncLock.AcquireReaderLock();
        try {
            return await extract(meta, recieveAsync);
        } finally {
            _asyncLock.ReleaseReaderLock();
        }
    }
    public async Task<bool> ContainsFileAsync(FileValue fileValue) {
        var meta = SingleStorageFileMeta.FromFileValue(fileValue);
        await _asyncLock.AcquireReaderLock();
        try {
            long pos = meta.Offset;
            long length;
            Guid id;
            lock (_fileLock) id = file.GetGuid(ref pos);
            if (id != meta.Id) return false;
            lock (_fileLock) length = file.GetVerifiedLong(ref pos);
            if (length != meta.Length) return false;
            lock (_fileLock) file.GetString(ref pos);
            var remaining = length + byteLengthOfMD5Checksum + _fileEndMarker.Length;
            if (pos + remaining > file.Length) return false; // file not long enough
            return true; // seems to be a valid file
        } finally {
            _asyncLock.ReleaseReaderLock();
        }
    }
    public async Task<FileValue> InsertAsync(IReadStream stream, string? fileName = null) {
        if (fileName == null) fileName = "noname";
        var readAsync = (byte[] buffer) => {
            // buffer returned must be exact size, buffer is not reused here
            var count = Math.Min(buffer.Length, (int)stream.Length);
            var bytes = stream.Read(count);
            return Task.FromResult(bytes);
        };
        var meta = await insertWithWriteLock(readAsync, stream.Length, fileName);
        return meta.ToFileValue(Id);
    }
    public async Task<FileValue> InsertAsync(Stream stream, string? fileName = null) {
        if (fileName == null) fileName = "noname";
        var readAsync = async (byte[] buffer) => {
            // reuse buffer if same size and allocate new buffer if different size as returned buffer must be exact size
            var count = Math.Min(buffer.Length, (int)stream.Length);
            var bytesRead = await stream.ReadAsync(buffer, 0, count);
            if (bytesRead < buffer.Length) {
                var temp = new byte[bytesRead];
                Array.Copy(buffer, temp, bytesRead);
                buffer = temp;
            }
            return buffer;
        };
        var meta = await insertWithWriteLock(readAsync, stream.Length, fileName);
        return meta.ToFileValue(Id);
    }
    public async Task ExtractAsync(FileValue value, IAppendStream stream) {
        var meta = SingleStorageFileMeta.FromFileValue(value);
        await extractWithReadLock(meta, (buffer) => {
            stream.Append(buffer);
            return Task.CompletedTask;
        });
    }
    public async Task ExtractAsync(FileValue value, Stream stream) {
        var meta = SingleStorageFileMeta.FromFileValue(value);
        await extractWithReadLock(meta, (buffer) => stream.WriteAsync(buffer, 0, buffer.Length));
    }
    public Task DeleteAsync(FileValue value) {
        // no action. File is never deleted from store, just left unused
        return Task.CompletedTask;
    }
    public long GetSizeForMetrics() {
        lock (_fileLock) {
            if (_file == null) return _ioProvider.GetFileSizeOrZeroIfUnknown(_fileKey);
            return _file.Length;
        }
    }
    public void Dispose() {
        _file?.Dispose();
        _asyncLock.Dispose();
        _ioProvider.CloseAllOpenStreams();
    }
}

