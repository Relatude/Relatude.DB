using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.FileConversion;

public class FileConversionEngine : IDisposable {
    const string _cacheBaseFolder = FileKeyUtility.ConvertedFolderName;
    static string[] _tempBaseFolder = [_cacheBaseFolder, FileConversionCache.TempFolderName];
    readonly FileConverterLibrary _fileConverters;
    readonly RunningConversions _conversions;
    readonly FileConversionCache _fileCache;
    readonly FileConversionScheduler _scheduler;
    readonly string? _localTempFolderPath;
    public readonly IDataStore Store;
    public FileConversionEngine(IDataStore store, IFileConverter[] converters, IIOProvider io) {
        Store = store;
        _fileConverters = new(converters);
        foreach (var c in converters) c.Initialize(this);
        _conversions = new();
        if (io.TryGetLocalFolderPath(_tempBaseFolder, out var tempFolder)) {
            _localTempFolderPath = tempFolder;
        } else {
            _localTempFolderPath = null;
        }
        _fileCache = new(io, _cacheBaseFolder);
        _scheduler = new FileConversionScheduler(pulse, ex => Store.LogError("File conversion scheduler error: ", ex));
        _scheduler.Start();
    }
    /// <summary>Files in the temp folder not written to for this long are left over from conversions
    /// that never completed. A conversion writing its output keeps its file younger than this.</summary>
    static readonly TimeSpan _staleTempFileAge = TimeSpan.FromHours(1);
    /// <summary>
    /// Removes what conversions that never completed left in the temp folder. Only stale files: the
    /// store calls this on every open, reopens included, while the engine - which lives as long as the
    /// store - may be converting, and another process sharing the cache (an overlapping recycle, a
    /// scaled out instance) may be writing there too. Emptying the whole folder failed their conversions.
    /// </summary>
    public void ClearTempFolder() {
        int deleted = 0, kept = 0;
        if (_localTempFolderPath == null) {
            try {
                (deleted, kept) = _fileCache.DeleteStaleTempFiles(_staleTempFileAge);
            } catch (Exception ex) {
                Store.LogError("Failed to clear temp folder for file conversions. ", ex);
                return;
            }
        } else if (Directory.Exists(_localTempFolderPath)) {
            var cutoff = DateTime.UtcNow - _staleTempFileAge;
            foreach (var file in Directory.EnumerateFiles(_localTempFolderPath, "*", SearchOption.AllDirectories)) {
                try {
                    if (File.GetLastWriteTimeUtc(file) > cutoff) {
                        kept++;
                        continue;
                    }
                    File.Delete(file);
                    deleted++;
                } catch {
                    kept++; // held open: still being written
                }
            }
        } else {
            try {
                Directory.CreateDirectory(_localTempFolderPath);
            } catch (Exception ex) {
                Store.LogError("Failed to create temp folder for file conversions. ", ex);
            }
        }
        if (deleted + kept > 0) Store.Log(SystemLogEntryType.Info, "Cleared temp folder for file conversions. " + deleted + " stale files deleted, " + kept + " recent files kept. ");
    }
    bool tryReserveWork(ProgressEntry entry) {
        try {
            return _fileConverters.TryReserveWorkOnConverter(entry.FileInfo.Formats);
        } catch (Exception ex) {
            _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment, ex.Message);
            _conversions.Remove(entry, ConversionStatus.Failed, "Failed to start conversion: " + ex.Message);
            return false;
        }
    }
    void pulse() {
        while (_conversions.TryGetWorkIfNotAlreadyWorkingOnEntryOrConverterTooBusy(out var entry, tryReserveWork)) {
            ThreadPool.QueueUserWorkItem(async _ => {
                try {
                    if (entry.Started == null) {
                        entry.Started = DateTime.UtcNow;
                        entry.Stopwatch = Stopwatch.StartNew();
                    }
                    // A request can miss the cache just before a run of the same conversion completes and
                    // queue it again, and another process sharing the cache may have converted it: either
                    // way it is done, and running it again would only redo the work.
                    var progress = _fileCache.TryGetStatusNoStream(entry.FileInfo.IdWithAdjustment, out var cached) && cached.Status == FileConversionStatus.Ready
                        ? cached
                        : await doConvertWork(entry);
                    switch (progress.Status) {
                        case FileConversionStatus.Ready:
                            entry.Stopwatch?.Stop();
                            entry.ProcessedMs = entry.Stopwatch?.Elapsed.TotalMilliseconds;
                            _conversions.Remove(entry, ConversionStatus.Completed, entry.ProgressInfo.Message);
                            break;
                        case FileConversionStatus.InProgress:
                            _conversions.UpdateIfExists(new(entry.Created, progress, entry.FileInfo, entry.InputSource, entry.Started, entry.Stopwatch?.Elapsed.TotalMilliseconds));
                            break;
                        case FileConversionStatus.Error: {
                                entry.Stopwatch?.Stop();
                                entry.ProcessedMs = entry.Stopwatch?.Elapsed.TotalMilliseconds;
                                bool cancelationRequested = false;
                                bool cancelationRequestedPermanently = false;
                                var key = entry.FileInfo.IdWithAdjustment.GetKey();
                                lock (_cancellationRequested) {
                                    cancelationRequested = _cancellationRequested.TryGetValue(key, out cancelationRequestedPermanently);
                                }
                                if (cancelationRequested) {
                                    if (cancelationRequestedPermanently) {
                                        _conversions.Remove(entry, ConversionStatus.Canceled, "Conversion permanently canceled by user. ");
                                        _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment, progress.Message ?? "Canceled permanently. ", permanent: true);
                                    } else {
                                        _conversions.Remove(entry, ConversionStatus.Canceled, "Conversion canceled by user. ");
                                    }
                                    lock (_cancellationRequested) {
                                        _cancellationRequested.Remove(key);
                                    }
                                } else {
                                    _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment, progress.Message ?? "Failed. ");
                                    _conversions.Remove(entry, ConversionStatus.Failed, progress.Message);
                                }
                            }
                            break;
                        default:
                            throw new Exception("Unknown status: " + progress.Status);
                    }
                } catch (Exception ex) {
                    bool cancelationRequested = false;
                    bool cancelationRequestedPermanently = false;
                    var key = entry.FileInfo.IdWithAdjustment.GetKey();
                    lock (_cancellationRequested) {
                        cancelationRequested = _cancellationRequested.TryGetValue(key, out cancelationRequestedPermanently);
                    }
                    if (cancelationRequested || ex is OperationCanceledException) {
                        string msg;
                        if (cancelationRequestedPermanently) {
                            msg = ex is OperationCanceledException excan ? "Conversion permanently canceled: " + excan.Message : "Conversion permanently canceled by user. ";
                            _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment, "Canceled permanently. ", permanent: true);
                        } else {
                            msg = ex is OperationCanceledException excan ? "Conversion canceled: " + excan.Message : "Conversion canceled by user. ";
                        }
                        _conversions.Remove(entry, ConversionStatus.Canceled, msg);
                        lock (_cancellationRequested) {
                            _cancellationRequested.Remove(key);
                        }
                    } else {
                        Store.LogError("Error during file conversion for file " + entry.FileInfo.IdWithAdjustment.GetKey() + ": ", ex);
                        var safePublicMessage = ex is FileNotFoundException ? "Source file missing" : "Conversion failed";
                        _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment, safePublicMessage);
                        _conversions.Remove(entry, ConversionStatus.Failed, ex.Message);
                    }
                }
                _fileConverters.ReleaseWorkFromConverter(entry.FileInfo.Formats);
                _conversions.RegisterNotDoingWorkOnEntry(entry);
            });
        }
        if (_conversions.Count > 0) {
            _scheduler.RunSoon();
        } else {
            _conversions.RemoveExpired();
        }
    }
    async Task<FileConversionProgressInfo> doConvertWork(ProgressEntry entry) {
        if (!_fileConverters.TryGetConverter(entry.FileInfo.Formats, out var converter)) {
            throw new Exception("No converter available for " + entry.FileInfo.Formats.ToString()?.ToUpper());
        }
        var conversionResult = await converter.DoConvertWork(entry.InputSource, entry.FileInfo);
        if (conversionResult.Output != null) {
            await _fileCache.SetFromStreamAsync(entry.FileInfo.IdWithAdjustment, conversionResult.Output);
        } else if (conversionResult.LocalFilePathOutput != null) {
            await _fileCache.SetFromFileAsync(entry.FileInfo.IdWithAdjustment, conversionResult.LocalFilePathOutput);
        } else {
            throw new Exception("Converter did not return output stream or file path for " + entry.FileInfo.Formats.From.ToString().ToUpper() + " to " + entry.FileInfo.Formats.To.ToString().ToUpper());
        }
        return conversionResult.ProgressInfo;
    }
    int adjustMaxWaitMs(FileConversionInfo info, int maxWaitMs) {
        if (maxWaitMs > 120000) return 120000; // cap max wait to 2 minutes to avoid too long waits
        if (maxWaitMs > -1) return maxWaitMs;
        var baseFrom = FileFormatUtil.GetFileType(info.Formats.From);
        var baseTo = FileFormatUtil.GetFileType(info.Formats.To);
        if (baseFrom == FileType.Image && baseTo == FileType.Image) {
            return 10000;
        }
        if (baseFrom == FileType.Video && baseTo == FileType.Image) {
            return 0;
        }
        if (baseFrom == FileType.Video && baseTo == FileType.Meta) {
            return 0;
        }
        if (baseFrom == FileType.Image && baseTo == FileType.Meta) {
            return 0;
        }
        return 0;
    }
    public bool TryGetProgressInfo(FileConversionInfo info, bool startIfNotFound, InputFileSource source, [MaybeNullWhen(false)] out FileConversionProgressInfo progressInfo) {
        var key = info.IdWithAdjustment.GetKey();
        if (_fileCache.TryGetStatusNoStream(info.IdWithAdjustment, out var progress)) {
            progressInfo = progress;
            return true;
        }
        if (_conversions.TryGet(key, out var entry)) {
            progressInfo = entry.ProgressInfo;
            return true;
        }
        if (startIfNotFound) {
            var prg = new FileConversionProgressInfo(FileConversionStatus.InProgress);
            _conversions.AddIfMissing(key, () => new(DateTime.UtcNow, prg, info, source, null, null));
            _scheduler.RunSoon();
            progressInfo = prg;
            return true;
        }
        progressInfo = null;
        return false;
    }
    public async Task<FileConversionResultAndStream> TryGetFormatAndStreamAsync(FileConversionInfo info, int maxWaitMs, InputFileSource source) {
        maxWaitMs = adjustMaxWaitMs(info, maxWaitMs);
        var key = info.IdWithAdjustment.GetKey();
        if (_fileCache.TryGetResultAndStream(info.IdWithAdjustment, out var result)) return result; // check cache first
        var sw = Stopwatch.StartNew();
        ProgressEntry? entry;
        _conversions.AddIfMissing(key, () => new(DateTime.UtcNow, new(FileConversionStatus.InProgress), info, source, null, null));
        _scheduler.RunSoon();
        if (!_fileConverters.TryGetConverter(info.Formats, out var converter)) {
            return new(new(FileConversionStatus.Error, 0, 0, "No converter available from " + info.Formats.From.ToString().ToUpper() + " to " + info.Formats.To.ToString().ToUpper() + ". "), null);
        }
        while (_conversions.TryGet(key, out entry)) {
            if (sw.ElapsedMilliseconds >= maxWaitMs) break;
            var remaining = maxWaitMs - sw.ElapsedMilliseconds;
            var min = sw.ElapsedMilliseconds switch { < 100 => 10, < 1000 => 25, < 5000 => 100, _ => 500 };
            var delay = (int)Math.Min(min, remaining);
            if (delay <= 0) break;
            await Task.Delay(delay);
        }
        if (_fileCache.TryGetResultAndStream(info.IdWithAdjustment, out result)) return result;
        if (entry != null) return new(entry.ProgressInfo, null);
        return new(new(FileConversionStatus.Error, 0, 0, "Unknown status"), null);
    }
    // The scheduler's heartbeat is a Timer, and a live Timer is rooted by the runtime's timer queue.
    // Leaving it running keeps this engine - and through it the whole data store, indexes and WAL
    // buffers included - alive for the rest of the process, so a store that is opened and closed
    // repeatedly never gives its memory back. Stopping it here is what releases that graph.
    public void Dispose() {
        _disposed = true;
        _scheduler.Stop();
        _conversions.ClearAll();
        _statusCache.ClearAll_NotSize0();
    }
    Cache<Guid, byte[]> _statusCache = new(1024 * 1024 * 10); // 10mb for status responses, which are usually small and can be expensive to generate
    /// <summary>
    /// The status picture under the key, rendered when it is not cached. Stored with its real size:
    /// Cache.GetOrCreate stores size 0, which the cache reserves for entries it must never evict, so
    /// the 10mb budget never applied and every status ever rendered (each progress step, remaining
    /// time and canvas size) stayed for the life of the process.
    /// </summary>
    Stream statusResponse(string uniqueStatusKey, Func<byte[]> render) {
        var key = uniqueStatusKey.GenerateHashGuid();
        if (!_statusCache.TryGet(key, out var bytes)) {
            bytes = render();
            _statusCache.Set(key, bytes, Math.Max(1, bytes.Length));
        }
        return new MemoryStream(bytes, writable: false); // the cached array, shared by every request
    }
    /// <summary>A status picture stands in for the result at its size, but is never rendered larger than
    /// this on either side (keeping the aspect): the browser scales it, and a canvas of the largest
    /// size an adjustment allows takes hundreds of megabytes to render.</summary>
    const int _maxStatusSide = 1920;

    // status response colors, image or video
    const string errorBgColor = "#FFBBBB";
    const string errorTextColor = "#330000";
    const string inProgressBgColor = "#FFFF99";
    const string inProgressTextColor = "#333300";
    const string readyBgColor = "#99FF99";
    const string readyTextColor = "#005500";
    const string unknownBgColor = "#777777";
    const string unknownTextColor = "#FFFFFF";

    public Stream GetStatusDataStream(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
        try {
            return getStatusDataStream(fileValue, adj, status);
        } catch (Exception err) {
            // error, with fallbacks to base formats
            var baseRequestedFormat = FileFormatUtil.GetFileType(adj.RequestedFormat);
            var text = new List<string> { "UNSUPPORTED CONVERSION", string.Empty, err.Message };
            var uniqueStatusKey = string.Join("|", text);
            if (baseRequestedFormat == FileType.Image && _fileConverters.TryGetConverter(new(FileFormat.Png), out var imgConv)) {
                return statusResponse(uniqueStatusKey,
                    () => imgConv.CreateStatusResponse(FileFormat.Png, 320, 240, text, errorTextColor, errorBgColor)
                );
            } else if (baseRequestedFormat == FileType.Video && _fileConverters.TryGetConverter(new(FileFormat.Mp4), out var vidConv)) {
                return statusResponse(uniqueStatusKey,
                    () => vidConv.CreateStatusResponse(FileFormat.Mp4, 320, 240, text, errorTextColor, errorBgColor)
                );
            } else {
                // no converter to represent base format requested
                throw;
            }
        }
    }
    Stream getStatusDataStream(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
        var baseRequestedFormat = FileFormatUtil.GetFileType(fileValue.Format);
        if (!_fileConverters.TryGetConverter(new FormatPair(fileValue.Format, adj.RequestedFormat), out var converter)) {
            throw new Exception(
                $"File format {fileValue.Format.ToString().ToUpper()} cannot be converted to {adj.RequestedFormat.ToString().ToUpper()}."
                + " There are no converters loaded that support this conversion. ");
        }
        int width = (adj as FileAdjustmentVideo)?.Width ?? (adj as FileAdjustmentImage)?.Width ?? 320;
        int height = (adj as FileAdjustmentVideo)?.Height ?? (adj as FileAdjustmentImage)?.Height ?? 240;
        
        
        
        // the size can come from a URL, and a status picture is only text: no more than 2048 pixels a side
        double fit = Math.Min(1, 2048.0 / Math.Max(1, Math.Max(width, height)));
        width = Math.Max(1, (int)(width * fit));
        height = Math.Max(1, (int)(height * fit));

        if (width > _maxStatusSide || height > _maxStatusSide) {
            var scale = Math.Min((double)_maxStatusSide / width, (double)_maxStatusSide / height);
            width = Math.Max(1, (int)(width * scale));
            height = Math.Max(1, (int)(height * scale));
        }




        // avoid looking for better status if generating status is CPU costly, cache key will be more coarse, thus less costly generations
        var lookForBetterStatus = baseRequestedFormat switch {
            FileType.Image => true, // Images are not expensive
            FileType.Video => width < 1000 && height < 800,
            _ => false
        };
        var key = FileIdWithAdjustment.KeyOf(fileValue, adj); // the key the running conversion reports under
        if (lookForBetterStatus && converter.TryGetLiveStatus(key, out var betterStatus)) {
            status = betterStatus;
        }
        var fillColor = status.Status switch {
            FileConversionStatus.Error => errorBgColor,
            FileConversionStatus.InProgress => inProgressBgColor,
            FileConversionStatus.Ready => readyBgColor,
            _ => unknownBgColor
        };
        var textColor = status.Status switch {
            FileConversionStatus.Error => errorTextColor,
            FileConversionStatus.InProgress => inProgressTextColor,
            FileConversionStatus.Ready => readyTextColor,
            _ => unknownTextColor
        };
        List<string> text = [];

        text.Add("CONVERSION " + status.Status.ToString().Decamelize().ToUpper());
        text.Add(string.Empty);
        text.Add(string.IsNullOrWhiteSpace(status.Message) ? "Please wait..." : status.Message);
        text.Add(string.Empty);

        // rounding of Progress to nearest 5% to avoid too many updates for small changes
        var progressPercentage = (int)(Math.Round(status.ProgressPercentage / 5.0) * 5);
        if (progressPercentage > 0) {
            text.Add(string.Empty);
            text.Add($"Progress: {progressPercentage}%");
        }

        // rounding of RemainingSeconds: nearest 5s for <30s, nearest 30s for <120s, nearest 60s otherwise
        var remainingSeconds = status.RemainingSeconds switch {
            < 30 => (int)(Math.Round(status.RemainingSeconds / 5.0) * 5),
            < 120 => (int)(Math.Round(status.RemainingSeconds / 30.0) * 30),
            _ => (int)(Math.Round(status.RemainingSeconds / 60.0) * 60)
        };
        if (remainingSeconds > 0) {
            var timeInText = remainingSeconds < 60 ? $"{remainingSeconds}s" :
                remainingSeconds < 3600 ? $"{remainingSeconds / 60}m" :
                $"{remainingSeconds / 3600}h";
            text.Add($"Remaining: {timeInText}");
        }

        var uniqueStatusKey = string.Join("|", text) + "|" + adj.RequestedFormat.ToString().ToUpper() + "|" + width + "x" + height;
        return statusResponse(uniqueStatusKey,
            () => converter.CreateStatusResponse(adj.RequestedFormat, width, height, text, textColor, fillColor)
        );
    }
    public void Start() {
        if (_disposed) return; // never resurrect the heartbeat of a disposed engine
        _scheduler.Start();
    }
    public void Stop() => _scheduler.Stop();
    bool _disposed;
    public void ClearCache(FileIdWithAdjustment id) => _fileCache.Clear(id);
    public void ClearAllCache() => _fileCache.ClearAll();
    public void ClearQueue() {
        _conversions.ClearAll();
    }
    /// <summary>The id file URLs derive their version from, kept with the converted files and replaced
    /// when they are all deleted. See <see cref="FileConversionCache.Generation"/>.</summary>
    public Guid CacheGeneration => _fileCache.Generation;
    public FileConverterLibrary ConverterLibrary => _fileConverters;
    public string LocalTempFolderPath => _localTempFolderPath ?? throw new Exception("Local temp folder path is not available. ");

    public FileConversions GetConversions() {
        var running = _conversions.GetAll();
        foreach (var conversion in running) {
            if (_fileConverters.TryGetConverter(new FormatPair(conversion.FromFormat, conversion.ToFormat), out var converter)) {
                if (converter.TryGetLiveStatus(conversion.Id, out var status)) {
                    if (status.Message != null) conversion.Description = status.Message;
                    conversion.ProgressPercentage = status.ProgressPercentage;
                }
            }
        }
        return new FileConversions(
            _conversions.Completed,
            _conversions.Failed,
            _conversions.Canceled,
            running.Count(c => c.Status == ConversionStatus.Queued),
            running.Count(c => c.Status == ConversionStatus.Running),
            running.ToArray()
        );
    }
    public bool CanConvert(FileFormat format, FileFormat requestedFormat) {
        return _fileConverters.TryGetConverter(new FormatPair(format, requestedFormat), out _);
    }
    /// <summary>
    /// Converts a file that is not one of the database's own - one code holds, such as a video whose
    /// sound the FileToText provider sends - now, by the converter that does the conversion: not queued
    /// behind the database's own conversions, not cached, and not listed among them. The result is a
    /// file in <see cref="LocalTempFolderPath"/>, which the caller reads and deletes. Throws
    /// <see cref="NotSupportedException"/> when no converter does the conversion, and
    /// <see cref="InvalidOperationException"/> with the converter's reason when it fails. Cancelling
    /// asks the converter to stop.
    /// </summary>
    public async Task<string> ConvertFileAsync(string inputPath, FileFormat from, FileAdjustmentBase adjustment, CancellationToken cancellationToken = default) {
        var adj = adjustment.Normalized();
        if (!_fileConverters.TryGetConverter(new FormatPair(from, adj.RequestedFormat), out var converter)) {
            throw new NotSupportedException($"No file converter here converts {from.ToString().ToUpper()} to {adj.RequestedFormat.ToString().ToUpper()}. ");
        }
        // an id of its own, for no file and no property, under which the converter runs this once
        var id = new FileIdWithAdjustment(Guid.NewGuid(), adj, new PropertyPath(Guid.Empty, Guid.Empty));
        var info = new FileConversionInfo(id, Path.GetFileName(inputPath), string.Empty, from);
        var source = new InputFileSource(() => Task.FromResult<Stream>(File.OpenRead(inputPath)), inputPath);
        ConversionProgress result;
        using (cancellationToken.Register(() => _ = converter.CancelAsync(id.GetKey()))) {
            result = await converter.DoConvertWork(source, info);
        }
        if (cancellationToken.IsCancellationRequested || result.ProgressInfo.Status != FileConversionStatus.Ready) {
            if (result.LocalFilePathOutput is { } left) tryDelete(left);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(result.ProgressInfo.Message ?? $"The conversion from {from.ToString().ToUpper()} to {adj.RequestedFormat.ToString().ToUpper()} failed. ");
        }
        if (result.LocalFilePathOutput is { } path) return path;
        if (result.Output is not { } output) throw new InvalidOperationException("The converter gave no result. ");
        Directory.CreateDirectory(LocalTempFolderPath);
        var file = Path.Combine(LocalTempFolderPath, Guid.NewGuid() + (FileFormatUtil.GetExtensionWithDot(adj.RequestedFormat) ?? ".bin"));
        await using (output)
        await using (var target = File.Create(file)) {
            await output.CopyToAsync(target, cancellationToken);
        }
        return file;

        static void tryDelete(string file) {
            try { File.Delete(file); } catch { }
        }
    }
    public void ClearAllErrors() {
        _fileCache.ClearAllErrors();
    }

    Dictionary<Guid, bool> _cancellationRequested = new();
    public async Task CancelRunning(Guid conversionId, bool permanently) {
        lock (_cancellationRequested) {
            _cancellationRequested[conversionId] = permanently;
        }
        if (_conversions.TryGet(conversionId, out var entry)) {
            if (entry.ProgressInfo.Status == FileConversionStatus.InProgress) {
                if (_conversions.DoingWorkNow(entry)) {
                    if (_fileConverters.TryGetConverter(entry.FileInfo.Formats, out var converter)) {
                        await converter.CancelAsync(conversionId);
                    } else {
                        // should not happen
                    }
                } else {
                    _conversions.Remove(entry, ConversionStatus.Canceled, "Conversion canceled by user. ");
                    if (permanently) {
                        _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment, "Canceled permanently. ", permanent: true);
                    }
                }
            }
        }
    }
}
