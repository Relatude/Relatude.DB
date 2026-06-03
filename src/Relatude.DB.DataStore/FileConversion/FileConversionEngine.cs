using Relatude.DB.Common;
using Relatude.DB.DataStores;
using Relatude.DB.IO;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.FileConversion;

public class FileConversionEngine : IDisposable {
    const string _cacheBaseFolder = "converted";
    static string[] _tempBaseFolder = [_cacheBaseFolder, "temp"];
    readonly FileConverterLibrary _fileConverters;
    readonly RunningConversions _conversions;
    readonly FileConversionCache _fileCache;
    readonly FileConversionScheduler _scheduler;
    readonly string _localTempFolderPath;
    readonly IDataStore _store;
    public FileConversionEngine(IDataStore store, IFileConverter[] converters, IIOProvider io, FileKeyUtility fileKeys) {
        _store = store;
        _fileConverters = new(converters);
        foreach (var c in converters) c.Initialize(this);
        _conversions = new();
        if (io.TryGetLocalFolderPath(_tempBaseFolder, out var tempFolder)) {
            _localTempFolderPath = tempFolder;
        } else {
            _localTempFolderPath = Path.GetTempPath();
        }
        _fileCache = new(io, _cacheBaseFolder);
        _scheduler = new FileConversionScheduler(pulse, ex => _store.LogError("File conversion scheduler error: ", ex));
        _scheduler.Start();
    }
    public void ClearTempFolder() {
        if (Directory.Exists(_localTempFolderPath)) {
            var fileCount = Directory.GetFiles(_localTempFolderPath).Length;
            _store.Log(SystemLogEntryType.Info, "Clearing temp folder for file conversions. " + fileCount + " files to delete. ");
            try {
                Directory.Delete(_localTempFolderPath, true);
            } catch (Exception ex) {
                _store.LogError("Failed to clear temp folder for file conversions. ", ex);
            }
        } else {
            _store.Log(SystemLogEntryType.Info, "Clearing temp folder for file conversions. 0 files to delete. ");
        }
        if (!Directory.Exists(_localTempFolderPath)) Directory.CreateDirectory(_localTempFolderPath);
    }
    bool tryReserveWork(ProgressEntry entry) {
        try {
            return _fileConverters.TryReserveWorkOnConverter(entry.FileInfo.Formats);
        } catch (Exception ex) {
            _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment.GetKey(), ex.Message);
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
                    var progress = await doConvertWork(entry);
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
                                        _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment.GetKey(), progress.Message ?? "Canceled permanently. ");
                                    } else {
                                        _conversions.Remove(entry, ConversionStatus.Canceled, "Conversion canceled by user. ");
                                    }
                                    lock (_cancellationRequested) {
                                        _cancellationRequested.Remove(key);
                                    }
                                } else {
                                    _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment.GetKey(), progress.Message ?? "Failed. ");
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
                            _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment.GetKey(), "Canceled permanently. ");
                        } else {
                            msg = ex is OperationCanceledException excan ? "Conversion canceled: " + excan.Message : "Conversion canceled by user. ";
                        }
                        _conversions.Remove(entry, ConversionStatus.Canceled, msg);
                        lock (_cancellationRequested) {
                            _cancellationRequested.Remove(key);
                        }
                    } else {
                        _store.LogError("Error during file conversion for file " + entry.FileInfo.IdWithAdjustment.GetKey() + ": ", ex);
                        var safePublicMessage = ex is FileNotFoundException ? "Source file missing" : "Conversion failed";
                        _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment.GetKey(), safePublicMessage);
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
            return 20000;
        }
        return 0;
    }
    public bool TryGetProgressInfo(FileConversionInfo info, bool startIfNotFound, InputFileSource source, [MaybeNullWhen(false)] out FileConversionProgressInfo progressInfo) {
        var key = info.IdWithAdjustment.GetKey();
        if (_fileCache.TryGetStatusNoStream(key, out var progress)) {
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
        if (_fileCache.TryGetResultAndStream(key, out var result)) return result; // check cache first
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
            var min = sw.ElapsedMilliseconds switch { < 100 => 20, < 1000 => 100, < 5000 => 500, _ => 1000 };
            await Task.Delay((int)Math.Min(min, remaining));
        }
        if (_fileCache.TryGetResultAndStream(key, out result)) return result;
        if (entry != null) return new(entry.ProgressInfo, null);
        return new(new(FileConversionStatus.Error, 0, 0, "Unknown status"), null);
    }
    public void Dispose() {
    }
    Cache<Guid, byte[]> _statusCache = new(1024 * 1024 * 10); // 10mb for status responses, which are usually small and can be expensive to generate

    // status response colors, image or video
    const string errorBgColor = "#FFBBBB";
    const string errorTextColor = "#330000";
    const string inProgressBgColor = "#FFFF99";
    const string inProgressTextColor = "#333300";
    const string readyBgColor = "#99FF99";
    const string readyTextColor = "#005500";
    const string unknownBgColor = "#777777";
    const string unknownTextColor = "#FFFFFF";

    public Stream GetStatusDataStream(FileValue fileValue, FileAdjustment adj, FileConversionProgressInfo status) {
        try {
            return getStatusDataStream(fileValue, adj, status);
        } catch (Exception err) {
            // error, with fallbacks to base formats
            var baseRequestedFormat = FileFormatUtil.GetFileType(adj.RequestedFormat);
            var text = new List<string> { "UNSUPPORTED CONVERSION", string.Empty, err.Message };
            var uniqueStatusKey = string.Join("|", text);
            if (baseRequestedFormat == FileType.Image && _fileConverters.TryGetConverter(new(FileFormat.Png), out var imgConv)) {
                var bytes = _statusCache.GetOrCreate(uniqueStatusKey.GenerateHashGuid(),
                    () => imgConv.CreateStatusResponse(FileFormat.Png, 320, 240, text, errorTextColor, errorBgColor)
                );
                return new MemoryStream(bytes);
            } else if (baseRequestedFormat == FileType.Video && _fileConverters.TryGetConverter(new(FileFormat.Mp4), out var vidConv)) {
                var bytes = _statusCache.GetOrCreate(uniqueStatusKey.GenerateHashGuid(),
                    () => vidConv.CreateStatusResponse(FileFormat.Mp4, 320, 240, text, errorTextColor, errorBgColor)
                );
                return new MemoryStream(bytes);
            } else {
                // no converter to represent base format requested
                throw;
            }
        }
    }
    Stream getStatusDataStream(FileValue fileValue, FileAdjustment adj, FileConversionProgressInfo status) {
        var baseRequestedFormat = FileFormatUtil.GetFileType(fileValue.Format);
        if (!_fileConverters.TryGetConverter(new FormatPair(fileValue.Format, adj.RequestedFormat), out var converter)) {
            throw new Exception(
                $"File format {fileValue.Format.ToString().ToUpper()} cannot be converted to {adj.RequestedFormat.ToString().ToUpper()}."
                + " There are no converters loaded that support this conversion. ");
        }
        int width = (adj as FileAdjustmentVideo)?.Width ?? (adj as FileAdjustmentImage)?.Width ?? 320;
        int height = (adj as FileAdjustmentVideo)?.Height ?? (adj as FileAdjustmentImage)?.Height ?? 240;

        // avoid looking for better status if generating status is CPU costly, cache key will be more coarse, thus less costly generations
        var lookForBetterStatus = baseRequestedFormat switch {
            FileType.Image => true, // Images are not expensive
            FileType.Video => width < 1000 && height < 800,
            _ => false
        };
        var key = fileValue.FileId.CombineHashGuid(adj.GetKey());
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

        var uniqueStatusKey = string.Join("|", text);
        var bytes = _statusCache.GetOrCreate(uniqueStatusKey.GenerateHashGuid(),
            () => converter.CreateStatusResponse(adj.RequestedFormat, width, height, text, textColor, fillColor)
        );
        return new MemoryStream(bytes);
    }
    public void Start() => _scheduler.Start();
    public void Stop() => _scheduler.Stop();
    public void ClearCache(Guid key) => _fileCache.Clear(key);
    public void ClearAllCache() => _fileCache.ClearAll();
    public void ClearQueue() {
        _conversions.ClearAll();
    }
    public FileConverterLibrary ConverterLibrary => _fileConverters;
    public string LocalTempFolderPath => _localTempFolderPath;

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
                        _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment.GetKey(), "Canceled permanently. ");
                    }
                }
            }
        }
    }
}
