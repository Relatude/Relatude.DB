using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using static Relatude.DB.IO.IOBenchmarkOptions;

namespace Relatude.DB.IO;

public static class IOBenchmark {
    public static readonly string[] Folder = [FileKeyUtility.UploadFolderName];

    public static Task<IOBenchmarkReport> RunAsync(IIOProvider io, IOBenchmarkOptions options, int testDurationInMs = 5000,
        IProgress<IOBenchmarkProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(() => new IOBenchmarkRun(io, options, testDurationInMs, progress, cancellationToken).Run(),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}

sealed class IOBenchmarkRun {
    const int pageSize = 4096;
    const int pageHeaderBytes = sizeof(long);
    const double preparationWeight = 1.5;
    const int createdFileBytes = 4 * KB;
    readonly IIOProvider _io;
    readonly IOBenchmarkOptions _options;
    readonly int _durationMs;
    readonly IProgress<IOBenchmarkProgress>? _progress;
    readonly CancellationToken _cancellation;
    readonly IOBenchmarkReport _report;
    readonly List<PlannedTest> _plan = [];
    readonly double _testWeight;
    readonly double _totalWeight;
    readonly byte[] _data;
    readonly byte[] _pattern = new byte[MB];
    readonly byte[] _expected = new byte[MB];
    readonly Random _random = new();
    readonly string _prefix = "io-benchmark-" + Guid.NewGuid().ToString("N")[..12];
    readonly List<string[]> _created = [];
    readonly long _progressIntervalTicks = Stopwatch.Frequency / 10;
    double _doneWeight;
    string[]? _readFile;
    long _readFileBytes;
    string? _preparationError;
    string _lastStep = string.Empty;
    bool _folderEnsured;
    long _lastProgress;

    sealed record PlannedTest(string Id, string Group, string Name, string Description, int BlockSize, double Weight, bool ReadsTestFile, Action<IOBenchmarkContext> Body);

    public IOBenchmarkRun(IIOProvider io, IOBenchmarkOptions options, int testDurationInMs, IProgress<IOBenchmarkProgress>? progress, CancellationToken cancellation) {
        _io = io;
        _options = options;
        _durationMs = Math.Max(500, testDurationInMs);
        _progress = progress;
        _cancellation = cancellation;
        _report = new IOBenchmarkReport {
            Kind = options.Kind,
            Location = options.Location,
            StartedUtc = DateTime.UtcNow,
            TestDurationMs = _durationMs,
            TimerResolutionUs = 1_000_000.0 / Stopwatch.Frequency,
        };
        _report.Notes.AddRange(options.Notes);
        var largestWrite = options.WriteSizes.Concat(options.CommitSizes).Append(options.DurableStreamSize).Append(createdFileBytes).Max();
        _data = new byte[largestWrite];
        _random.NextBytes(_data);
        _random.NextBytes(_pattern);
        plan();
        _testWeight = _plan.Sum(t => t.Weight);
        _totalWeight = _testWeight + (_plan.Any(t => t.ReadsTestFile) ? preparationWeight : 0);
    }

    void plan() {
        var file = SizeLabel(_options.ReadFileBytes);
        var recycle = SizeLabel(_options.RecycleFileBytes);
        foreach (var size in _options.WriteSizes) {
            var label = SizeLabel(size);
            add("write-" + size, IOBenchmarkGroups.Write, $"Append {label}",
                $"Appends {label} at a time to a new file and hands it to the storage with a flush, started again on a new file every {recycle}. Nothing is made durable.",
                size, 1, false, c => write(c, size));
        }
        if (_options.MeasureDurableWrites) {
            foreach (var size in _options.CommitSizes) {
                var label = SizeLabel(size);
                add("commit-" + size, IOBenchmarkGroups.Durable, $"Commit {label}",
                    $"Appends {label} and makes it durable before the next one, the way the database commits a transaction.",
                    size, 1, false, c => commit(c, size));
            }
            var stream = SizeLabel(_options.DurableStreamSize);
            var every = SizeLabel(_options.DurableFlushBytes);
            add("durable-stream", IOBenchmarkGroups.Durable, $"Stream {stream}, durable every {every}",
                $"Appends {stream} at a time and makes the file durable every {every}: the sustained write speed of the storage itself.",
                _options.DurableStreamSize, 1.5, false, durableStream);
        }
        foreach (var size in _options.SequentialReadSizes) {
            var label = SizeLabel(size);
            add("sequential-" + size, IOBenchmarkGroups.Sequential, $"Read {label} in order",
                $"Reads the {file} test file from start to end, {label} at a time, the way the database reads its log and state when it opens.",
                size, 1, true, c => sequential(c, size));
        }
        foreach (var size in _options.ScatteredReadSizes) {
            var label = SizeLabel(size);
            add("scattered-" + size, IOBenchmarkGroups.Scattered, $"Read {label} at random",
                $"Reads {label} from random places in the {file} test file, the way the database reads nodes that are not in its cache.",
                size, 1, true, c => scattered(c, size));
        }
        foreach (var custom in _options.CustomTests) {
            add(custom.Id, custom.Group, custom.Name, custom.Description, custom.BlockSize, custom.Weight, true, custom.Run);
        }
        add("file-exists", IOBenchmarkGroups.Files, "Check that a file exists", "Asks the storage whether the test file exists.", 0, 0.5, true, fileExists);
        add("file-size", IOBenchmarkGroups.Files, "Read a file's size", "Asks the storage how big the test file is.", 0, 0.5, true, fileSize);
        add("file-open", IOBenchmarkGroups.Files, "Open and close a file", "Opens the test file for reading and closes it again without reading.", 0, 0.5, true, fileOpen);
        add("file-create", IOBenchmarkGroups.Files, $"Create, write {SizeLabel(createdFileBytes)} and delete",
            $"Creates a new file, writes {SizeLabel(createdFileBytes)} to it, closes it and deletes it again.", createdFileBytes, 0.5, true, fileCreate);
        add("folder-list", IOBenchmarkGroups.Files, "List a folder", "Lists the folder the test files are in, with its files.", 0, 0.5, true, folderList);
    }

    void add(string id, string group, string name, string description, int blockSize, double weight, bool readsTestFile, Action<IOBenchmarkContext> body) =>
        _plan.Add(new PlannedTest(id, group, name, description, blockSize, weight, readsTestFile, body));

    public IOBenchmarkReport Run() {
        var watch = Stopwatch.StartNew();
        try {
            if (_options.CannotRun != null) {
                _report.Error = _options.CannotRun;
                return _report;
            }
            _io.EnsureFolder(IOBenchmark.Folder);
            _folderEnsured = true;
            foreach (var test in _plan) {
                if (_cancellation.IsCancellationRequested) break;
                if (test.ReadsTestFile && _readFile == null && _preparationError == null) prepare();
                run(test);
                _doneWeight += test.Weight;
            }
        } catch (OperationCanceledException) {
        } catch (Exception error) {
            _report.Error = error.Message;
        } finally {
            cleanUp();
            finish(watch);
        }
        return _report;
    }

    void prepare() {
        var size = Math.Max(2L * MB, _options.ReadFileBytes / MB * MB);
        var step = $"Writing the {SizeLabel(size)} test file";
        var key = file("read");
        var started = Stopwatch.GetTimestamp();
        try {
            report(0, step, true);
            using (var stream = _io.OpenAppend(key)) {
                var block = new byte[MB];
                for (long position = 0; position < size; position += MB) {
                    _cancellation.ThrowIfCancellationRequested();
                    fillExpected(block, position);
                    stream.Append(block);
                    report(preparationWeight * position / size, step);
                }
                stream.Flush(true);
            }
            _readFile = key;
            _readFileBytes = size;
            _report.FileBytes = size;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception error) {
            _preparationError = error.Message;
        } finally {
            _report.PreparationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _doneWeight += preparationWeight;
        }
    }

    void run(PlannedTest test) {
        var result = new IOBenchmarkTestResult {
            Id = test.Id,
            Group = test.Group,
            Name = test.Name,
            Description = test.Description,
            BlockSize = test.BlockSize,
        };
        if (test.ReadsTestFile && _readFile == null) {
            result.Error = "The test file could not be written" + (_preparationError != null ? ": " + _preparationError : ".");
            _report.Tests.Add(result);
            return;
        }
        var step = IOBenchmarkGroups.TitleOf(test.Group) + ": " + test.Name;
        var sliceTicks = (long)(_durationMs * test.Weight / _testWeight * Stopwatch.Frequency / 1000);
        var meter = new IOBenchmarkMeter(sliceTicks, _cancellation, fraction => report(fraction * test.Weight, step));
        var context = new IOBenchmarkContext(_io, _readFile ?? [], _readFileBytes, meter, _random, matches, _cancellation);
        var before = counters();
        report(0, step, true);
        try {
            test.Body(context);
        } catch (OperationCanceledException) {
        } catch (Exception error) {
            result.Error = error.Message;
        }
        meter.CompleteInto(result);
        var after = counters();
        if (before is IOCounters b && after is IOCounters a) {
            var moved = new IOCounters(a.Reads - b.Reads, a.Writes - b.Writes, a.BytesRead - b.BytesRead, a.BytesWritten - b.BytesWritten);
            if (moved != default) result.Storage = moved;
        }
        if (result.Operations > 0 || result.Error != null) _report.Tests.Add(result);
    }

    void finish(Stopwatch watch) {
        _report.ElapsedMs = watch.Elapsed.TotalMilliseconds;
        _report.Cancelled = _cancellation.IsCancellationRequested;
        if (_report.Cancelled) _report.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"Stopped before the end: {_report.Tests.Count} of {_plan.Count} tests ran."));
        var verified = _report.Tests.Sum(t => t.Verified);
        var mismatches = _report.Tests.Sum(t => t.Mismatches);
        if (mismatches > 0) {
            _report.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"{mismatches:N0} of {verified:N0} checked reads returned bytes that differ from what was written."));
        } else if (verified > 0) {
            _report.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"Every one of the {verified:N0} reads that were checked returned exactly the bytes that were written."));
        }
        _progress?.Report(new IOBenchmarkProgress(100, _report.Cancelled ? "Cancelled" : "Done"));
    }

    void cleanUp() {
        foreach (var key in _created) deleteQuietly(key);
        if (!_folderEnsured) return;
        try {
            _io.DeleteFolderIfEmpty(IOBenchmark.Folder);
        } catch { }
    }

    void report(double partialWeight, string step, bool force = false) {
        if (_progress == null) return;
        var now = Stopwatch.GetTimestamp();
        if (!force && step == _lastStep && now - _lastProgress < _progressIntervalTicks) return;
        _lastProgress = now;
        _lastStep = step;
        var percent = _totalWeight > 0 ? Math.Min(100, (_doneWeight + partialWeight) / _totalWeight * 100) : 100;
        _progress.Report(new IOBenchmarkProgress(percent, step));
    }

    IOCounters? counters() {
        try {
            return _io.Counters;
        } catch {
            return null;
        }
    }

    string[] file(string role) {
        string[] key = [.. IOBenchmark.Folder, $"{_prefix}-{role}"];
        if (!_created.Any(k => k.IsSameKey(key))) _created.Add(key);
        return key;
    }

    void deleteQuietly(string[] key) {
        try {
            _io.DeleteFileIfItExists(key);
        } catch { }
    }

    IAppendStream startOver(IAppendStream stream, string[] key) {
        stream.Dispose();
        _io.DeleteFileIfItExists(key);
        return _io.OpenAppend(key);
    }

    void fillExpected(Span<byte> destination, long position) {
        Span<byte> header = stackalloc byte[pageHeaderBytes];
        var written = 0;
        while (written < destination.Length) {
            var at = position + written;
            var inPage = (int)(at % pageSize);
            var run = Math.Min(destination.Length - written, pageSize - inPage);
            _pattern.AsSpan((int)(at % _pattern.Length), run).CopyTo(destination.Slice(written, run));
            if (inPage < pageHeaderBytes) {
                BinaryPrimitives.WriteInt64LittleEndian(header, at / pageSize);
                var headerRun = Math.Min(pageHeaderBytes - inPage, run);
                header.Slice(inPage, headerRun).CopyTo(destination.Slice(written, headerRun));
            }
            written += run;
        }
    }

    bool matches(ReadOnlySpan<byte> data, long position) {
        while (data.Length > 0) {
            var run = Math.Min(data.Length, _expected.Length);
            var expected = _expected.AsSpan(0, run);
            fillExpected(expected, position);
            if (!data[..run].SequenceEqual(expected)) return false;
            data = data[run..];
            position += run;
        }
        return true;
    }

    void write(IOBenchmarkContext c, int size) {
        var meter = c.Meter;
        var key = file("write");
        var stream = _io.OpenAppend(key);
        long inFile = 0;
        try {
            while (meter.Continue()) {
                var started = IOBenchmarkMeter.Now;
                stream.Append(_data, size);
                meter.Record(started, size);
                inFile += size;
                if (inFile < _options.RecycleFileBytes) continue;
                var flushing = IOBenchmarkMeter.Now;
                stream.Flush(false);
                meter.Include(flushing);
                stream = startOver(stream, key);
                inFile = 0;
            }
            var last = IOBenchmarkMeter.Now;
            stream.Flush(false);
            meter.Include(last);
        } finally {
            stream.Dispose();
            deleteQuietly(key);
        }
    }

    void commit(IOBenchmarkContext c, int size) {
        var meter = c.Meter;
        var key = file("commit");
        var stream = _io.OpenAppend(key);
        long inFile = 0;
        try {
            while (meter.Continue()) {
                var started = IOBenchmarkMeter.Now;
                stream.Append(_data, size);
                stream.Flush(true);
                meter.Record(started, size);
                inFile += size;
                if (inFile < _options.RecycleFileBytes) continue;
                stream = startOver(stream, key);
                inFile = 0;
            }
        } finally {
            stream.Dispose();
            deleteQuietly(key);
        }
    }

    void durableStream(IOBenchmarkContext c) {
        var meter = c.Meter;
        var size = _options.DurableStreamSize;
        var key = file("durable");
        var stream = _io.OpenAppend(key);
        long inFile = 0, pending = 0;
        try {
            while (meter.Continue()) {
                var started = IOBenchmarkMeter.Now;
                stream.Append(_data, size);
                pending += size;
                if (pending >= _options.DurableFlushBytes) {
                    stream.Flush(true);
                    pending = 0;
                }
                meter.Record(started, size);
                inFile += size;
                if (inFile < _options.RecycleFileBytes) continue;
                if (pending > 0) {
                    var flushing = IOBenchmarkMeter.Now;
                    stream.Flush(true);
                    meter.Include(flushing);
                    pending = 0;
                }
                stream = startOver(stream, key);
                inFile = 0;
            }
            if (pending > 0) {
                var last = IOBenchmarkMeter.Now;
                stream.Flush(true);
                meter.Include(last);
            }
        } finally {
            stream.Dispose();
            deleteQuietly(key);
        }
    }

    void sequential(IOBenchmarkContext c, int size) {
        var meter = c.Meter;
        var buffer = new byte[size];
        long reads = 0;
        while (meter.Continue()) {
            using var stream = _io.OpenRead(c.ReadFileKey, 0);
            long position = 0;
            while (stream.More() && meter.Continue()) {
                var started = IOBenchmarkMeter.Now;
                var read = stream.ReadInto(buffer);
                meter.Record(started, read);
                if (read <= 0) break;
                if (reads++ % 8 == 0) meter.Verify(matches(buffer.AsSpan(0, read), position));
                position += read;
            }
        }
    }

    void scattered(IOBenchmarkContext c, int size) {
        var meter = c.Meter;
        var buffer = new byte[size];
        var range = c.ReadFileBytes - size;
        using var stream = _io.OpenAppend(c.ReadFileKey);
        while (meter.Continue()) {
            var position = _random.NextInt64(0, range + 1);
            var started = IOBenchmarkMeter.Now;
            stream.Get(position, size, buffer);
            meter.Record(started, size);
            meter.Verify(matches(buffer, position));
        }
    }

    void fileExists(IOBenchmarkContext c) {
        while (c.Meter.Continue()) {
            var started = IOBenchmarkMeter.Now;
            if (!_io.Exists(c.ReadFileKey)) throw new IOException("The storage says the test file does not exist. ");
            c.Meter.Record(started, 0);
        }
    }

    void fileSize(IOBenchmarkContext c) {
        while (c.Meter.Continue()) {
            var started = IOBenchmarkMeter.Now;
            var length = _io.GetFileSizeOrZeroIfUnknown(c.ReadFileKey);
            c.Meter.Record(started, 0);
            if (length != c.ReadFileBytes) throw new IOException(string.Create(CultureInfo.InvariantCulture, $"The storage says the test file is {length:N0} bytes, it was written as {c.ReadFileBytes:N0}. "));
        }
    }

    void fileOpen(IOBenchmarkContext c) {
        while (c.Meter.Continue()) {
            var started = IOBenchmarkMeter.Now;
            using (_io.OpenRead(c.ReadFileKey, 0)) { }
            c.Meter.Record(started, 0);
        }
    }

    void fileCreate(IOBenchmarkContext c) {
        var key = file("create");
        while (c.Meter.Continue()) {
            var started = IOBenchmarkMeter.Now;
            using (var stream = _io.OpenAppend(key)) stream.Append(_data, createdFileBytes);
            _io.DeleteFileIfItExists(key);
            c.Meter.Record(started, createdFileBytes);
        }
    }

    void folderList(IOBenchmarkContext c) {
        while (c.Meter.Continue()) {
            var started = IOBenchmarkMeter.Now;
            _io.GetFolderAsync(IOBenchmark.Folder, false, true).GetAwaiter().GetResult();
            c.Meter.Record(started, 0);
        }
    }
}
