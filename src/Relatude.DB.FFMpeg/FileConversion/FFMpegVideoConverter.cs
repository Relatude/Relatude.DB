using FFMpegCore;
using Relatude.DB.Common;
using Relatude.DB.FileConversion.ImageEncoder;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace Relatude.DB.FileConversion;

public class FFMpegVideoConverter : IFileConverter {
    public FFMpegVideoConverter(int? threadCount = null) {
        ThreadCount = threadCount.HasValue ? threadCount.Value : Math.Max(1, Environment.ProcessorCount / 8);
    }
    public int ThreadCount { get; set; }
    public int CallDelayMs { get; set; } = 0;

    static readonly FileFormat[] _videoIns = [FileFormat.Mp4, FileFormat.Avi, FileFormat.Mov, FileFormat.Wmv, FileFormat.Flv, FileFormat.Mkv];
    static readonly FileFormat[] _videoOuts = [FileFormat.Mp4, FileFormat.Avi, FileFormat.Mov, FileFormat.Wmv, FileFormat.Mkv];
    static readonly FileFormat[] _imageOuts = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Webp];
    static string _ffmpegBinDir = Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    static readonly SemaphoreSlim _downloadLock = new(1, 1);
    static bool _ffmpegBinReady;
    static string? _ffmpegBinProgressInfo;

    readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    readonly ConcurrentDictionary<Guid, FileConversionProgressInfo> _conversionProgress = new();

    FileConverterLibrary? _library;
    public void Initialize(FileConverterLibrary library) => _library = library;


    static async Task ensureFFMpegBinAsync() {
        if (_ffmpegBinReady) return;
        await _downloadLock.WaitAsync();
        try {
            if (_ffmpegBinReady) return;
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

    int concucurrentCount = 0;
    public async Task<ConversionProgress> DoConvertWork(InputFileSource source, FileConversionInfo info) {
        await ensureFFMpegBinAsync();
        Interlocked.Increment(ref concucurrentCount);
        var key = info.IdWithAdjustment.GetKey();
        var cts = new CancellationTokenSource();
        _cancellations[key] = cts;
        _conversionProgress[key] = new(FileConversionStatus.InProgress, 0);
        var inputTmp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{toExtension(info.FromFormat)}");
        var outputTmp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{toExtension(info.Formats.To)}");
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
                await extractThumbnailAsync(inputTmp, outputTmp, info, key, _conversionProgress, cts.Token);
            else
                await convertVideoAsync(inputTmp, outputTmp, info, key, _conversionProgress, cts.Token);

            var bytes = await File.ReadAllBytesAsync(outputTmp, cts.Token);
            return new ConversionProgress(new FileConversionProgressInfo(FileConversionStatus.Ready, 100), new MemoryStream(bytes));
        } catch (OperationCanceledException) {
            return new ConversionProgress(new FileConversionProgressInfo(FileConversionStatus.Error, 0, message: "Cancelled"));
        } catch (Exception ex) {
            return new ConversionProgress(new FileConversionProgressInfo(FileConversionStatus.Error, 0, message: ex.Message));
        } finally {
            _cancellations.TryRemove(key, out _);
            _conversionProgress.TryRemove(key, out _);
            tryDelete(inputTmp);
            tryDelete(outputTmp);
            Interlocked.Decrement(ref concucurrentCount);
        }
    }

    async Task extractThumbnailAsync(string inputTmp, string outputTmp, FileConversionInfo info,
        Guid key, ConcurrentDictionary<Guid, FileConversionProgressInfo> progress, CancellationToken ct) {
        var adj = info.IdWithAdjustment.Adjustment as FileAdjustmentImage;
        int? w = adj?.Width, h = adj?.Height;
        progress[key] = new(FileConversionStatus.InProgress, 10, message: "Seeking to frame...");
        TimeSpan? seekTo = await resolveSeekPosition(inputTmp, adj, ct);
        progress[key] = new(FileConversionStatus.InProgress, 50, message: "Extracting frame...");
        var processor = FFMpegArguments
            .FromFileInput(inputTmp, true, opts => { if (seekTo.HasValue) opts.Seek(seekTo.Value); })
            .OutputToFile(outputTmp, true, opts => {
                opts.WithCustomArgument("-vframes 1");
                if (w.HasValue || h.HasValue) opts.WithVideoFilters(vf => vf.Scale(w ?? -1, h ?? -1));
            });
        await processor.CancellableThrough(ct).ProcessAsynchronously();
        if (adj != null) {
            var needAdditionalProcessing =
                //adj.RequestedFormat != FileFormat.Png || 
                //adj.Quality.HasValue || 
                adj.Brightness.HasValue || adj.Contrast.HasValue ||
                adj.Saturation.HasValue || adj.Sharpness.HasValue || adj.HueShift.HasValue;
            // Console.WriteLine("Need additional processing: " + needAdditionalProcessing);
            if (needAdditionalProcessing) {
                if (_library == null) throw new InvalidOperationException("Converter library not initialized.");
                if (!_library.TryGetConverter(new(FileFormat.Png, FileFormat.Png), out var converter))
                    throw new InvalidOperationException("Converter library does not support required format for status response generation.");
                IImage img;
                var stream = File.OpenRead(outputTmp);
                try {
                    if (converter is ImageConverterBase imgConverter) {
                        img = imgConverter.Load(stream);
                    } else {
                        img = NativeImage.Load(stream);
                    }
                } finally {
                    stream.Dispose();
                }
                img = img.Adjust(adj);
                File.WriteAllBytes(outputTmp, img.Encode(adj.RequestedFormat, adj.Quality));
            }
        }

        progress[key] = new(FileConversionStatus.InProgress, 95, message: "Finalizing...");
    }

    static async Task<TimeSpan?> resolveSeekPosition(string inputTmp, FileAdjustmentImage? adj, CancellationToken ct) {
        var probe = await FFProbe.AnalyseAsync(inputTmp, cancellationToken: ct);
        TimeSpan? position = null;
        if (adj?.TimeOffsetMs.HasValue == true) {
            position = TimeSpan.FromMilliseconds(adj.TimeOffsetMs.Value);
        }
        if (adj?.TimeOffsetPercentage.HasValue == true) {
            var duration = probe.Duration;
            if (duration > TimeSpan.Zero) position = duration * (adj.TimeOffsetPercentage.Value / 100.0);
        }
        if (position.HasValue) {
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (position > probe.Duration) position = probe.Duration;
        }
        return null;
    }

    async Task convertVideoAsync(string inputTmp, string outputTmp, FileConversionInfo info,
        Guid key, ConcurrentDictionary<Guid, FileConversionProgressInfo> progress, CancellationToken ct) {
        var adj = info.IdWithAdjustment.Adjustment as FileAdjustmentVideo;
        progress[key] = new(FileConversionStatus.InProgress, 5, message: "Analyzing input...");
        var probe = await FFProbe.AnalyseAsync(inputTmp, cancellationToken: ct);
        var videoDuration = probe.Duration;
        var sw = Stopwatch.StartNew();
        progress[key] = new(FileConversionStatus.InProgress, 10, message: "Converting...");
        var processor = FFMpegArguments
            .FromFileInput(inputTmp)
            .OutputToFile(outputTmp, true, opts => {
                if (adj?.Width.HasValue == true || adj?.Height.HasValue == true)
                    opts.WithVideoFilters(vf => vf.Scale(adj.Width ?? -1, adj.Height ?? -1));
                if (adj?.TargetBitRateInMbps > 0)
                    opts.WithVideoBitrate((int)(adj.TargetBitRateInMbps * 1024));
            })
            .NotifyOnProgress(progressInPercentage => {
                double progressPerSec = progressInPercentage / sw.Elapsed.TotalSeconds;
                double remainingSecs = (100 - progressInPercentage) / progressPerSec;
                int progressToReport = (int)Math.Round(progressInPercentage);
                int remainingSecsToReport = progressToReport > 2 ? (int)Math.Round(remainingSecs) : 0;
                progress[key] = new(FileConversionStatus.InProgress, progressToReport, remainingSecsToReport, message: "Converting...");
            }, videoDuration);
        //Console.WriteLine($"Starting conversion for {info.FileName} (current concurrent: {concucurrentCount} Max: {MaxConcurrentWork})");
        await processor.CancellableThrough(ct).ProcessAsynchronously();
        progress[key] = new(FileConversionStatus.InProgress, 95, message: "Finalizing...");
    }

    static string toExtension(FileFormat fmt) => FileFormatUtil.GetExtensionWithDot(fmt) ?? ".tmp";

    static void tryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    public bool TryGetLiveStatus(Guid fileId, FileAdjustmentBase adj, [MaybeNullWhen(false)] out FileConversionProgressInfo status) {
        var key = fileId.CombineHashGuid(adj.GetKey());
        if (_conversionProgress.TryGetValue(key, out var liveProgress)) status = liveProgress;
        else if (!_ffmpegBinReady) status = new(FileConversionStatus.InProgress, 0, message: _ffmpegBinProgressInfo);
        else status = null;
        return status != null;
    }

    public byte[] CreateStatusResponse(FileFormat requestedFormat, int width, int height, List<string> text, string textColor, string fillColor) {
        var baseRequestFormat = FileFormatUtil.GetBaseFormatFromDetailedFormat(requestedFormat);
        if (_library == null) throw new InvalidOperationException("Converter library not initialized.");
        if (baseRequestFormat == FileType.Video) {
            ensureFFMpegBinAsync().Wait(); // inefficient but this is just for status generation and ensures progress info is updated
            int vw = width % 2 == 0 ? width : width + 1; // video dimensions must be even for most codecs
            int vh = height % 2 == 0 ? height : height + 1; // video dimensions must be even for most codecs
            if (!_library.TryGetConverter(new(FileFormat.Png, FileFormat.Png), out var converter))
                throw new InvalidOperationException("Converter library does not support required format for status response generation.");
            IImage img;
            if (converter is ImageConverterBase imgConverter) {
                img = imgConverter.Create(vw, vh).GetStatusImage(text, textColor, fillColor);
            } else {
                img = NativeImage.Create(vw, vh).GetStatusImage(text, textColor, fillColor);
            }
            var imgBytes = img.Encode(FileFormat.Png);
            var imgTmp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.png");
            var vidTmp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}{toExtension(requestedFormat)}");
            try {
                File.WriteAllBytes(imgTmp, imgBytes);
                FFMpegArguments
                    .FromFileInput(imgTmp, true, opts => opts.WithCustomArgument("-loop 1"))
                    .OutputToFile(vidTmp, true, opts => opts
                        .WithCustomArgument("-t 2")
                        .WithVideoCodec("libx264")
                        .WithCustomArgument("-pix_fmt yuv420p"))
                    .ProcessSynchronously();
                return File.ReadAllBytes(vidTmp);
            } finally {
                tryDelete(imgTmp);
                tryDelete(vidTmp);
            }
        } else if (baseRequestFormat == FileType.Image) {
            if (!_library.TryGetConverter(new(FileFormat.Png, FileFormat.Png), out var converter))
                throw new InvalidOperationException("Converter library does not support required format for status response generation.");
            IImage img;
            if (converter is ImageConverterBase imgConverter) {
                img = imgConverter.Create(width, height).GetStatusImage(text, textColor, fillColor);
            } else {
                img = NativeImage.Create(width, height).GetStatusImage(text, textColor, fillColor);
            }
            return img.Encode(requestedFormat);
        } else {
            throw new ArgumentException("Requested format must be a video format for status response generation.");
        }
    }

}
