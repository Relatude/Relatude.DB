using Relatude.DB.Common;
using Relatude.DB.IO;
using System.Collections;
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
                            _conversionsInProgress.UpdateIfExists(new(entry.Created, progress, entry.FileInfo, entry.GetInputStream));
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
        var conversionResult = await converter.DoConvertWork(entry.GetInputStream, entry.FileInfo);
        if (conversionResult.Output != null) {
            await _fileCache.SetFromStreamAsync(entry.FileInfo.IdWithAdjustment, conversionResult.Output);
        }
        return conversionResult.ProgressInfo;
    }
    public async Task<FileConversionResult> TryGetFormatAsync(FileConversionInfo info, int maxWaitMs, Func<Task<Stream>> getInputStream) {
        var key = info.IdWithAdjustment.GetKey();
        if (_fileCache.TryGetResult(key, out var result)) return result; // check cache first
        var sw = Stopwatch.StartNew();
        ProgressEntry? entry;
        _conversionsInProgress.AddIfMissing(key, () =>
            new(DateTime.UtcNow, new(FileConversionStatus.InProgress), info, getInputStream)
        );
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
    public Stream GetStatus(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
        if (_fileConverters.TryGetConverter(new FormatPair(adj.RequestedFormat, adj.RequestedFormat), out var converter)) {
            return converter.GetStatusRepresentation(fileValue, adj, status);
        } else {
            var baseFormat = FileFormatUtil.GetBaseFormatFromDetailedFormat(fileValue.Format);
            if (baseFormat == FileType.Image) {
                if (_fileConverters.TryGetConverter(new FormatPair(FileFormat.Png, FileFormat.Png), out converter)) {
                    return converter.GetStatusRepresentation(fileValue, adj, status);
                }
            }
        }
        throw new Exception("No converter available for status representation of format " + adj.RequestedFormat.ToString().ToUpper());
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
    public void KillRunning(Guid key) {
        throw new NotImplementedException();
        if (_conversionsInProgress.TryGet(key, out var entry)) {
            //_scheduler.TryStopIfRunning(key);
            //_conversionsInProgress.Remove(entry);
            //_fileCache.Clear(key);
        }
    }
}
