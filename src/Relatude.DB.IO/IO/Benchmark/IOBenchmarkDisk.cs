using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Relatude.DB.IO.IOBenchmarkOptions;

namespace Relatude.DB.IO;

static class IOBenchmarkDisk {
    const long minimumFreeBytes = 64L * MB;

    public static IOBenchmarkOptions Options(IOProviderDisk io, bool readOnly, int testDurationInMs) {
        var notes = new List<string>();
        var readFile = ScaledBytes(64L * MB, testDurationInMs, 512L * MB);
        var recycle = 64L * MB;
        string? cannotRun = readOnly ? "The storage is read only, so nothing can be written to it to measure." : null;
        if (driveOf(io.BaseFolder) is DriveInfo drive) {
            var free = drive.AvailableFreeSpace;
            notes.Add($"Drive {drive.Name}: {drive.DriveFormat}, {drive.DriveType.ToString().ToLowerInvariant()}, {Readable(free)} free of {Readable(drive.TotalSize)}.");
            if (free < minimumFreeBytes) {
                cannotRun ??= $"Only {Readable(free)} is free on {drive.Name}, which is not enough room for the test files.";
            } else if (free < (readFile + recycle) * 4) {
                readFile = Math.Max(4L * MB, free / 16 / MB * MB);
                recycle = Math.Max(4L * MB, free / 16 / MB * MB);
                notes.Add("The drive has little free space, so the test files were made smaller than usual.");
            }
        }
        notes.Add("Plain writes and reads go through the operating system's file cache, as the database's own do: the reads are served from memory once the test file has been written, and the writes measure how fast the system takes the data rather than how fast the drive stores it. Durable writes and reads past the cache are the drive itself.");
        return new IOBenchmarkOptions {
            Kind = "Local disk",
            Location = io.BaseFolder,
            CannotRun = cannotRun,
            ReadFileBytes = readFile,
            RecycleFileBytes = recycle,
            DurableFlushBytes = 16L * MB,
            CustomTests = IOBenchmarkUncached.Tests(notes),
            Notes = notes,
        };
    }

    static DriveInfo? driveOf(string folder) {
        try {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive : null;
        } catch {
            return null;
        }
    }
}

static unsafe class IOBenchmarkUncached {
    const FileOptions noBuffering = (FileOptions)0x20000000;
    const int alignment = 4096;
    const int readsBetweenCacheDrops = 256;
    const int posixDontNeed = 4;
    static readonly delegate* unmanaged<int, long, long, int, int> _posixFadvise = loadPosixFadvise();

    public static IReadOnlyList<IOBenchmarkCustomTest> Tests(List<string> notes) {
        if (OperatingSystem.IsWindows()) return tests(bypass: true);
        if (OperatingSystem.IsLinux() && _posixFadvise != null) {
            notes.Add("Reads past the cache: the test file is dropped from the page cache before each pass, so reads come from the drive; a random read that lands on a page read earlier in the same pass is served from memory.");
            return tests(bypass: false);
        }
        notes.Add("Reads past the cache are not measured on this operating system.");
        return [];
    }

    static IReadOnlyList<IOBenchmarkCustomTest> tests(bool bypass) => [
        new("uncached-sequential", IOBenchmarkGroups.Uncached, $"Read {SizeLabel(MB)} in order, uncached",
            "Reads the test file from start to end with the operating system's cache out of the way, so every byte comes from the drive.",
            MB, 1.5, c => sequential(c, MB, bypass)),
        new("uncached-4096", IOBenchmarkGroups.Uncached, $"Read {SizeLabel(4 * KB)} at random, uncached",
            "Reads 4 KB from random places with the cache out of the way: the drive's own access time, what a node read costs when nothing is cached.",
            4 * KB, 1.5, c => scattered(c, 4 * KB, bypass)),
        new("uncached-65536", IOBenchmarkGroups.Uncached, $"Read {SizeLabel(64 * KB)} at random, uncached",
            "Reads 64 KB from random places with the cache out of the way.",
            64 * KB, 1.5, c => scattered(c, 64 * KB, bypass)),
    ];

    static string localPath(IOBenchmarkContext c) =>
        c.Io.TryGetLocalFilePath(c.ReadFileKey, out var path) ? path : throw new IOException("The test file has no path on the drive. ");

    static SafeFileHandle open(IOBenchmarkContext c, bool bypass, FileOptions hint) =>
        File.OpenHandle(localPath(c), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bypass ? noBuffering : hint);

    static void sequential(IOBenchmarkContext c, int size, bool bypass) {
        var meter = c.Meter;
        using var handle = open(c, bypass, FileOptions.SequentialScan);
        using var buffer = new AlignedBuffer(size);
        var span = buffer.Span;
        long reads = 0;
        while (meter.Continue()) {
            if (!bypass) dropFromCache(handle);
            for (long position = 0; position < c.ReadFileBytes && meter.Continue();) {
                var started = IOBenchmarkMeter.Now;
                var read = RandomAccess.Read(handle, span, position);
                meter.Record(started, read);
                if (read <= 0) break;
                if (reads++ % 8 == 0) meter.Verify(c.MatchesReadFile(span[..read], position));
                position += read;
            }
        }
    }

    static void scattered(IOBenchmarkContext c, int size, bool bypass) {
        var meter = c.Meter;
        using var handle = open(c, bypass, FileOptions.RandomAccess);
        using var buffer = new AlignedBuffer(size);
        var span = buffer.Span;
        var pages = (c.ReadFileBytes - size) / alignment;
        var sinceDrop = 0;
        if (!bypass) dropFromCache(handle);
        while (meter.Continue()) {
            if (!bypass && ++sinceDrop >= readsBetweenCacheDrops) {
                dropFromCache(handle);
                sinceDrop = 0;
            }
            var position = c.Random.NextInt64(0, pages + 1) * alignment;
            var started = IOBenchmarkMeter.Now;
            var read = RandomAccess.Read(handle, span, position);
            meter.Record(started, read);
            meter.Verify(read == size && c.MatchesReadFile(span[..read], position));
        }
    }

    static void dropFromCache(SafeFileHandle handle) {
        if (_posixFadvise(checked((int)handle.DangerousGetHandle()), 0, 0, posixDontNeed) != 0) throw new IOException("The file could not be dropped from the page cache. ");
    }

    static delegate* unmanaged<int, long, long, int, int> loadPosixFadvise() {
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess) return null;
        foreach (var name in new[] { "libc.so.6", "libc.so", "libc" }) {
            if (NativeLibrary.TryLoad(name, out var library) && NativeLibrary.TryGetExport(library, "posix_fadvise", out var function)) {
                return (delegate* unmanaged<int, long, long, int, int>)function;
            }
        }
        return null;
    }

    sealed class AlignedBuffer : IDisposable {
        readonly void* _pointer;
        readonly int _length;
        public AlignedBuffer(int length) {
            _length = length;
            _pointer = NativeMemory.AlignedAlloc((nuint)length, alignment);
        }
        public Span<byte> Span => new(_pointer, _length);
        public void Dispose() => NativeMemory.AlignedFree(_pointer);
    }
}
