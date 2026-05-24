using FFMpegCore;
using Relatude.DB.Common;
using System.Collections.Concurrent;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace Relatude.DB.FileConversion;

public class FFMpegVideoConverter : IFileConverter {
    static readonly FileFormat[] _videoIns = [FileFormat.Mp4, FileFormat.Avi, FileFormat.Mov, FileFormat.Wmv, FileFormat.Flv, FileFormat.Mkv];
    static readonly FileFormat[] _videoOuts = [FileFormat.Mp4, FileFormat.Avi, FileFormat.Mov, FileFormat.Wmv, FileFormat.Mkv];
    static readonly FileFormat[] _imageOuts = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Webp];
    static string _ffmpegBinDir = Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    static readonly SemaphoreSlim _downloadLock = new(1, 1);
    static bool _ffmpegBinReady;
    static DateTime _ffmpegBinDownloadStartedAt;
    static string? _ffmpegBinProgressInfo;

    readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    readonly ConcurrentDictionary<Guid, FileConversionProgressInfo> _conversionProgress = new();

    public int MaxConcurrentWork { get; set; } = Math.Max(1, Environment.ProcessorCount / 4);
    public int MinIntervalBetweenCallsInMs { get; set; } = 0;

    static async Task ensureFFMpegBinAsync() {
        if (_ffmpegBinReady) return;
        await _downloadLock.WaitAsync();
        try {
            if (_ffmpegBinReady) return;
            _ffmpegBinDownloadStartedAt = DateTime.UtcNow;
            Directory.CreateDirectory(_ffmpegBinDir);
            _ffmpegBinProgressInfo = "Downloading FFmpeg...";
            await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official, _ffmpegBinDir,
                new Progress<ProgressInfo>(p => _ffmpegBinProgressInfo = $"Downloading FFmpeg: {p.DownloadedBytes / 1024} KB / {p.TotalBytes / 1024} KB"));
            GlobalFFOptions.Configure(opts => opts.BinaryFolder = _ffmpegBinDir);
            _ffmpegBinReady = true;
        } catch (Exception ex) {
            _ffmpegBinProgressInfo = "Error downloading FFmpeg: " + ex.Message;
        } finally {
            _downloadLock.Release();
        }
    }

    public bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed)
        => inBase == FileType.Video && _videoIns.Contains(inDetailed)
        && ((outBase == FileType.Video && _videoOuts.Contains(outDetailed))
            || (outBase == FileType.Image && _imageOuts.Contains(outDetailed)));

    public Task<bool> CancelAsync(Guid key) {
        if (_cancellations.TryRemove(key, out var cts)) { cts.Cancel(); cts.Dispose(); return Task.FromResult(true); }
        return Task.FromResult(false);
    }

    public async Task<ConversionProgress> DoConvertWork(InputFileSource source, FileConversionInfo info) {
        await ensureFFMpegBinAsync();
        var key = info.IdWithAdjustment.GetKey();
        var cts = new CancellationTokenSource();
        _cancellations[key] = cts;
        _conversionProgress[key] = new(FileConversionStatus.InProgress, 0);
        var inputTmp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.{ToExtension(info.FromFormat)}");
        var outputTmp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.{ToExtension(info.Formats.To)}");
        try {
            if (source.HasLocalFilePath) {
                var localPath = source.GetLocalFilePathOrThrow();
                File.Copy(localPath, inputTmp);
            } else {
                await using (var inp = await source.OpenInputStream())
                await using (var fs = File.Create(inputTmp))
                    await inp.CopyToAsync(fs, cts.Token);
            }

            if (_imageOuts.Contains(info.Formats.To))
                await ExtractThumbnailAsync(inputTmp, outputTmp, info, key, _conversionProgress, cts.Token);
            else
                await ConvertVideoAsync(inputTmp, outputTmp, info, key, _conversionProgress, cts.Token);

            var bytes = await File.ReadAllBytesAsync(outputTmp, cts.Token);
            return new ConversionProgress(new FileConversionProgressInfo(FileConversionStatus.Ready, 100), new MemoryStream(bytes));
        } catch (OperationCanceledException) {
            return new ConversionProgress(new FileConversionProgressInfo(FileConversionStatus.Error, 0, message: "Cancelled"));
        } catch (Exception ex) {
            return new ConversionProgress(new FileConversionProgressInfo(FileConversionStatus.Error, 0, message: ex.Message));
        } finally {
            _cancellations.TryRemove(key, out _);
            _conversionProgress.TryRemove(key, out _);
            TryDelete(inputTmp);
            TryDelete(outputTmp);
        }
    }

    static async Task ExtractThumbnailAsync(string inputTmp, string outputTmp, FileConversionInfo info,
        Guid key, ConcurrentDictionary<Guid, FileConversionProgressInfo> progress, CancellationToken ct) {
        var adj = info.IdWithAdjustment.Adjustment as FileAdjustmentImage;
        int? w = adj?.Width, h = adj?.Height;
        progress[key] = new(FileConversionStatus.InProgress, 10, message: "Seeking to frame...");
        TimeSpan? seekTo = await ResolveSeekPosition(inputTmp, adj, ct);
        progress[key] = new(FileConversionStatus.InProgress, 50, message: "Extracting frame...");
        var processor = FFMpegArguments
            .FromFileInput(inputTmp, true, opts => { if (seekTo.HasValue) opts.Seek(seekTo.Value); })
            .OutputToFile(outputTmp, true, opts => {
                opts.WithCustomArgument("-vframes 1");
                if (w.HasValue || h.HasValue) opts.WithVideoFilters(vf => vf.Scale(w ?? -1, h ?? -1));
            });
        await processor.CancellableThrough(ct).ProcessAsynchronously();
        progress[key] = new(FileConversionStatus.InProgress, 95, message: "Finalizing...");
    }

    static async Task<TimeSpan?> ResolveSeekPosition(string inputTmp, FileAdjustmentImage? adj, CancellationToken ct) {
        if (adj?.TimeOffsetMs.HasValue == true)
            return TimeSpan.FromMilliseconds(adj.TimeOffsetMs.Value);
        if (adj?.TimeOffsetPercentage.HasValue == true) {
            var probe = await FFProbe.AnalyseAsync(inputTmp, cancellationToken: ct);
            var duration = probe.Duration;
            if (duration > TimeSpan.Zero)
                return duration * (adj.TimeOffsetPercentage.Value / 100.0);
        }
        return null;
    }

    static async Task ConvertVideoAsync(string inputTmp, string outputTmp, FileConversionInfo info,
        Guid key, ConcurrentDictionary<Guid, FileConversionProgressInfo> progress, CancellationToken ct) {
        var adj = info.IdWithAdjustment.Adjustment as FileAdjustmentVideo;
        progress[key] = new(FileConversionStatus.InProgress, 5, message: "Analyzing input...");
        var probe = await FFProbe.AnalyseAsync(inputTmp, cancellationToken: ct);
        var totalDuration = probe.Duration;
        progress[key] = new(FileConversionStatus.InProgress, 10, message: "Converting...");
        var processor = FFMpegArguments
            .FromFileInput(inputTmp)
            .OutputToFile(outputTmp, true, opts => {
                if (adj?.Width.HasValue == true || adj?.Height.HasValue == true)
                    opts.WithVideoFilters(vf => vf.Scale(adj.Width ?? -1, adj.Height ?? -1));
                if (adj?.TargetBitRateInMbps > 0)
                    opts.WithVideoBitrate((int)(adj.TargetBitRateInMbps * 1024));
            })
            .NotifyOnProgress(pct => {
                var mapped = (int)Math.Clamp(pct * 0.85 + 10, 10, 95);
                var remainingSecs = totalDuration > TimeSpan.Zero ? (int)(totalDuration.TotalSeconds * (1 - pct / 100.0)) : -1;
                progress[key] = new(FileConversionStatus.InProgress, mapped, remaining: remainingSecs, message: "Converting...");
            }, totalDuration);
        await processor.CancellableThrough(ct).ProcessAsynchronously();
        progress[key] = new(FileConversionStatus.InProgress, 95, message: "Finalizing...");
    }

    public Stream GetStatus(FileValue fileValue, FileAdjustmentBase adj, FileConversionProgressInfo status) {
        int width = (adj as FileAdjustmentVideo)?.Width ?? (adj as FileAdjustmentImage)?.Width ?? 320;
        int height = (adj as FileAdjustmentVideo)?.Height ?? (adj as FileAdjustmentImage)?.Height ?? 240;
        var liveKey = fileValue.IsEmpty ? (Guid?)null : fileValue.FileId.CombineHashGuid(adj.GetKey());
        if (liveKey.HasValue && _conversionProgress.TryGetValue(liveKey.Value, out var liveProgress))
            status = liveProgress;
        else if (!_ffmpegBinReady)
            status = new(FileConversionStatus.InProgress, 0, message: _ffmpegBinProgressInfo);
        if (FileFormatUtil.GetBaseFormatFromDetailedFormat(adj.RequestedFormat) == FileType.Video) {
            ensureFFMpegBinAsync().Wait(); // inefficient but this is just for status generation and ensures progress info is updated
            int vw = width % 2 == 0 ? width : width + 1;
            int vh = height % 2 == 0 ? height : height + 1;
            var img = SkiaImage.Create(vw, vh).GetStatusImage(fileValue, adj, status);
            var imgBytes = img.Encode(FileFormat.Png);
            var imgTmp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");
            var vidTmp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.{ToExtension(adj.RequestedFormat)}");
            try {
                File.WriteAllBytes(imgTmp, imgBytes);
                FFMpegArguments
                    .FromFileInput(imgTmp, true, opts => opts.WithCustomArgument("-loop 1"))
                    .OutputToFile(vidTmp, true, opts => opts
                        .WithCustomArgument("-t 2")
                        .WithVideoCodec("libx264")
                        .WithCustomArgument("-pix_fmt yuv420p"))
                    .ProcessSynchronously();
                return new MemoryStream(File.ReadAllBytes(vidTmp));
            } finally {
                TryDelete(imgTmp);
                TryDelete(vidTmp);
            }
        } else {
            var img = SkiaImage.Create(width, height).GetStatusImage(fileValue, adj, status);
            return new MemoryStream(img.Encode(FileFormat.Png));
        }
    }

    static string ToExtension(FileFormat fmt) => fmt switch {
        FileFormat.Mp4 => "mp4",
        FileFormat.Avi => "avi",
        FileFormat.Mov => "mov",
        FileFormat.Wmv => "wmv",
        FileFormat.Flv => "flv",
        FileFormat.Jpeg => "jpg",
        FileFormat.Png => "png",
        FileFormat.Webp => "webp",
        _ => "tmp"
    };

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

}
