using Relatude.DB.Common;
using Relatude.DB.IO;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.FileConversion;

public class FileConversionEngine : IDisposable {
    readonly FileConverterLibrary _fileConverters;
    readonly RunningConversions _conversionsInProgress;
    readonly FileConversionCache _fileCache;
    readonly FileConversionScheduler _scheduler;
    public FileConversionEngine(IFileConverter[] converters, IIOProvider io, FileKeyUtility fileKeys) {
        _fileConverters = new(converters);
        foreach (var c in converters) c.Initialize(_fileConverters);
        _conversionsInProgress = new();
        _fileCache = new(io);
        _scheduler = new FileConversionScheduler(pulse, ex => Console.WriteLine(ex.ToString()));
        _scheduler.Start();
    }
    bool tryReserveWork(ProgressEntry entry) {
        return _fileConverters.TryReserveWorkOnConverter(entry.FileInfo.Formats);
    }
    void pulse() {
        while (_conversionsInProgress.TryGetWorkIfNotAlreadyWorkingOnEntryOrConverterTooBusy(out var entry, tryReserveWork)) {
            ThreadPool.QueueUserWorkItem(async _ => {
                try {
                    var progress = await doConvertWork(entry);
                    switch (progress.Status) {
                        case FileConversionStatus.Ready:
                            _conversionsInProgress.Remove(entry);
                            break;
                        case FileConversionStatus.InProgress:
                            _conversionsInProgress.UpdateIfExists(new(entry.Created, progress, entry.FileInfo, entry.InputSource));
                            break;
                        case FileConversionStatus.Error:
                            _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment.GetKey(), progress.Message ?? "Failed. ");
                            _conversionsInProgress.Remove(entry);
                            break;
                        default:
                            throw new Exception("Unknown status: " + progress.Status);
                    }
                } catch (Exception ex) {
                    _fileCache.SaveErrorStatus(entry.FileInfo.IdWithAdjustment.GetKey(), ex.Message);
                    _conversionsInProgress.Remove(entry);
                }
                _fileConverters.ReleaseWorkFromConverter(entry.FileInfo.Formats);
                _conversionsInProgress.RegisterNotDoingWorkOnEntry(entry);
            });
        }
        if (_conversionsInProgress.Count > 0) _scheduler.RunSoon();
    }
    async Task<FileConversionProgressInfo> doConvertWork(ProgressEntry entry) {
        if (!_fileConverters.TryGetConverter(entry.FileInfo.Formats, out var converter)) {
            throw new Exception("No converter available for " + entry.FileInfo.Formats.ToString()?.ToUpper());
        }
        var conversionResult = await converter.DoConvertWork(entry.InputSource, entry.FileInfo);
        if (conversionResult.Output != null) {
            await _fileCache.SetFromStreamAsync(entry.FileInfo.IdWithAdjustment, conversionResult.Output);
        }
        return conversionResult.ProgressInfo;
    }
    int adjustMaxWaitMs(FileConversionInfo info, int maxWaitMs) {
        if (maxWaitMs > 120000) return 120000; // cap max wait to 2 minutes to avoid too long waits
        if (maxWaitMs > -1) return maxWaitMs;
        var baseFrom = FileFormatUtil.GetBaseFormatFromDetailedFormat(info.Formats.From);
        var baseTo = FileFormatUtil.GetBaseFormatFromDetailedFormat(info.Formats.To);
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
        if (_fileCache.TryGetResult(key, out var result)) {
            progressInfo = result.ProgressInfo;
            return true;
        }
        if (_conversionsInProgress.TryGet(key, out var entry)) {
            progressInfo = entry.ProgressInfo;
            return true;
        }
        if (startIfNotFound) {
            var prg = new FileConversionProgressInfo(FileConversionStatus.InProgress);
            _conversionsInProgress.AddIfMissing(key, () => new(DateTime.UtcNow, prg, info, source));
            progressInfo = prg;
            return true;
        }
        progressInfo = null;
        return false;
    }
    public async Task<FileConversionResult> TryGetFormatAsync(FileConversionInfo info, int maxWaitMs, InputFileSource source) {
        maxWaitMs = adjustMaxWaitMs(info, maxWaitMs);
        var key = info.IdWithAdjustment.GetKey();
        if (_fileCache.TryGetResult(key, out var result)) return result; // check cache first
        var sw = Stopwatch.StartNew();
        ProgressEntry? entry;
        _conversionsInProgress.AddIfMissing(key, () => new(DateTime.UtcNow, new(FileConversionStatus.InProgress), info, source));
        _scheduler.RunSoon();
        if (!_fileConverters.TryGetConverter(info.Formats, out var converter)) {
            return new(new(FileConversionStatus.Error, 0, 0, "No converter available from " + info.Formats.From.ToString().ToUpper() + " to " + info.Formats.To.ToString().ToUpper() + ". "), null);
        }
        while (_conversionsInProgress.TryGet(key, out entry)) {
            if (sw.ElapsedMilliseconds >= maxWaitMs) break;
            var remaining = maxWaitMs - sw.ElapsedMilliseconds;
            var min = sw.ElapsedMilliseconds switch { < 100 => 20, < 1000 => 100, < 5000 => 500, _ => 1000 };
            await Task.Delay((int)Math.Min(min, remaining));
        }
        if (_fileCache.TryGetResult(key, out result)) return result;
        if (entry != null) return new(entry.ProgressInfo, null);
        return new(new(FileConversionStatus.Error, 0, 0, "Unknown status"), null);
    }
    public void Dispose() {
    }
    Cache<Guid, byte[]> _statusCache = new(1024 * 1024 * 10); // 10mb for status responses, which are usually small and can be expensive to generate
    public Stream GetStatusDataStream(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
        if (!_fileConverters.TryGetConverter(new FormatPair(fileValue.Format, adj.RequestedFormat), out var converter)) {
            throw new Exception("No converter available for status representation of format " + adj.RequestedFormat.ToString().ToUpper());
        }
        var baseRequestedFormat = FileFormatUtil.GetBaseFormatFromDetailedFormat(fileValue.Format);
        int width = (adj as FileAdjustmentVideo)?.Width ?? (adj as FileAdjustmentImage)?.Width ?? 320;
        int height = (adj as FileAdjustmentVideo)?.Height ?? (adj as FileAdjustmentImage)?.Height ?? 240;

        // avoid looking for better status if generating status is CPU costly, cache key will be more coarse, thus less costly generations
        var lookForBetterStatus = baseRequestedFormat switch {
            FileType.Image => true, // Images are not expensive
            FileType.Video => width < 1000 && height < 800,
            _ => false
        };
        if (lookForBetterStatus && converter.TryGetBetterStatusOnRunning(fileValue, adj, out var betterStatus)) {
            status = betterStatus;
        }
        var fillColor = status.Status switch {
            FileConversionStatus.Error => "#FF9999",
            FileConversionStatus.InProgress => "#FFFF99",
            FileConversionStatus.Ready => "#99FF99",
            _ => "#777777"
        };
        var textColor = status.Status switch {
            FileConversionStatus.Error => "#550000",
            FileConversionStatus.InProgress => "#333300",
            FileConversionStatus.Ready => "#005500",
            _ => "#FFFFFF"
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
        _conversionsInProgress.ClearAll();
    }
    public int QueueCount => _conversionsInProgress.Count;
    public ProgressEntry[] GetRunning() => _conversionsInProgress.GetAll();
}
