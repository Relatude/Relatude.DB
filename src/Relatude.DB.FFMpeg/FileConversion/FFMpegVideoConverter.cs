using FFMpegCore;
using FFMpegCore.Enums;
using Relatude.DB.Common;
using Relatude.DB.FileConversion.ImageEncoders;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;

namespace Relatude.DB.FileConversion;

public class FFMpegVideoConverter : IFileConverter {
    public FFMpegVideoConverter(int? threadCount = null) {
        //ThreadCount = threadCount.HasValue ? threadCount.Value : Math.Max(1, Environment.ProcessorCount / 8);
        ThreadCount = threadCount.HasValue ? threadCount.Value : 1;
    }
    public int ThreadCount { get; set; }
    public int CallDelayMs { get; set; } = 0;

    static readonly FileFormat[] _videoIns = [FileFormat.Mp4, FileFormat.Avi, FileFormat.Mov, FileFormat.Wmv, FileFormat.Flv, FileFormat.Mkv];
    static readonly FileFormat[] _videoOuts = [FileFormat.Mp4, FileFormat.Avi, FileFormat.Mov, FileFormat.Wmv, FileFormat.Mkv];
    static readonly FileFormat[] _imageIns = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Webp, FileFormat.Avif];
    static readonly FileFormat[] _imageOuts = [FileFormat.Jpeg, FileFormat.Png, FileFormat.Webp, FileFormat.Avif];
    static readonly FileFormat[] _metaOuts = [FileFormat.FileMetaJson];
    static string _ffmpegBinDir = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
    public bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed) {
        if (inBase == FileType.Video) {
            if (!_videoIns.Contains(inDetailed)) return false; // unsupported input video format
            if (outBase == FileType.Video) return _videoOuts.Contains(outDetailed); // supported video to video
            if (outBase == FileType.Meta) return _metaOuts.Contains(outDetailed); // supported video to meta 
            if (outBase == FileType.Image) return _imageOuts.Contains(outDetailed); // supported video to image (thumbnail)
        }
        if (inBase == FileType.Image) {
            if (!_imageIns.Contains(inDetailed)) return false; // unsupported input image format
            if (outBase == FileType.Image) return _imageOuts.Contains(outDetailed); // supported image to image
        }
        return false;
    }

    static readonly SemaphoreSlim _downloadLock = new(1, 1);
    static bool _ffmpegBinReady;
    static string? _ffmpegBinProgressInfo;


    readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    readonly ConcurrentDictionary<Guid, FileConversionProgressInfo> _conversionProgress = new();

    FileConversionEngine? _engine;
    FileConversionEngine engine => _engine ?? throw new ArgumentNullException("Not initialized. ");
    public void Initialize(FileConversionEngine conversionEngine) => _engine = conversionEngine;
    public bool tryGetConverter(FileFormat from, FileFormat to, [MaybeNullWhen(false)] out IFileConverter converter) {
        return engine.ConverterLibrary.TryGetConverter(new FormatPair(from, to), out converter);
    }
    string getTempPath(FileFormat format) => Path.Combine(engine.LocalTempFolderPath, $"{Guid.NewGuid()}{FileFormatUtil.GetExtensionWithDot(format)}");

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


    public Task<bool> CancelAsync(Guid key) {
        if (_cancellations.TryRemove(key, out var cts)) {
            cts.Cancel(); cts.Dispose();
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public async Task<ConversionProgress> DoConvertWork(InputFileSource source, FileConversionInfo info) {
        await ensureFFMpegBinAsync();
        var key = info.IdWithAdjustment.GetKey();
        var cts = new CancellationTokenSource();
        _cancellations[key] = cts;
        _conversionProgress[key] = new(FileConversionStatus.InProgress, 0);
        string inputFilePath = string.Empty;
        bool deleteInputFile = false;
        var outputFilePath = getTempPath(info.Formats.To);
        try {
            if (source.HasLocalFilePath) {
                deleteInputFile = false;
                inputFilePath = source.GetLocalFilePathOrThrow();
            } else {
                deleteInputFile = true;
                inputFilePath = getTempPath(info.Formats.From);
                var folder = Path.GetDirectoryName(inputFilePath);
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder!);
                await using (var inp = await source.OpenInputStream())
                await using (var fs = File.Create(inputFilePath))
                    await inp.CopyToAsync(fs, cts.Token);
            }
            var typeFrom = FileFormatUtil.GetFileType(info.Formats.From);
            var typeTo = FileFormatUtil.GetFileType(info.Formats.To);
            if (typeFrom == FileType.Video) {
                if (typeTo == FileType.Image) { // video to image -> thumbnail
                    var imgAdj = (FileAdjustmentImage)info.IdWithAdjustment.Adjustment;
                    await extractThumbnailAndProcessAsync(inputFilePath, outputFilePath, imgAdj, key, _conversionProgress, cts.Token);
                } else if (typeTo == FileType.Video) { // video to video conversion
                    await convertVideoAsync(inputFilePath, outputFilePath, info, key, _conversionProgress, cts.Token);
                } else if (typeTo == FileType.Meta) { // video to video conversion
                    await extractMetaAsync(inputFilePath, outputFilePath, cts.Token);
                } else {
                    throw new NotSupportedException("Unsupported output file type: " + typeTo);
                }
            } else if (typeFrom == FileType.Image) {
                if (typeTo == FileType.Image) {
                    // image to image conversion with possible adjustments
                    var imgAdj = (FileAdjustmentImage)info.IdWithAdjustment.Adjustment;
                    await processImageAsync(inputFilePath, outputFilePath, imgAdj, key, _conversionProgress, cts.Token);
                } else {
                    throw new NotSupportedException("Unsupported output file type: " + typeTo);
                }
            } else {
                throw new NotSupportedException("Unsupported input file type: " + typeFrom);
            }
            return new(new(FileConversionStatus.Ready, 100), null, outputFilePath);
        } catch (Exception ex) {
            tryDelete(outputFilePath);
            return new(new(FileConversionStatus.Error, 0, message: ex.Message));
        } finally {
            _cancellations.TryRemove(key, out _);
            _conversionProgress.TryRemove(key, out _);
            if (deleteInputFile) tryDelete(inputFilePath);
        }
    }
    async Task extractMetaAsync(string inputTmp, string outputTmp, CancellationToken ct) {
        var probe = await FFProbe.AnalyseAsync(inputTmp, cancellationToken: ct);
        var meta = new BasicFileMeta {
            Width = probe.PrimaryVideoStream?.Width ?? 0,
            Height = probe.PrimaryVideoStream?.Height ?? 0,
            Duration = probe.Duration,
            FormatDetails = probe.Format.FormatLongName,
            AllMetaJson = JsonSerializer.Serialize(probe)
        };
        File.WriteAllBytes(outputTmp, meta.ToBytes());
    }

    async Task processImageAsync(string inputTmp, string outputTmp, FileAdjustmentImage adj,
        Guid key, ConcurrentDictionary<Guid, FileConversionProgressInfo> progress, CancellationToken ct) {

        var needPostProcessing = // these are not supported by ffmpeg filters
            adj.Brightness.HasValue || adj.Contrast.HasValue ||
            adj.Saturation.HasValue || adj.Sharpness.HasValue || adj.HueShift.HasValue;

        IFileConverter? postConverter = null;
        if (needPostProcessing) {
            if (!tryGetConverter(FileFormat.Png, FileFormat.Png, out postConverter))
                throw new InvalidOperationException("Converter library does not support required format for status response generation.");
        }

        string outputForFfmpeg = needPostProcessing ? getTempPath(FileFormat.Png) : outputTmp;

        try {

            progress[key] = new(FileConversionStatus.InProgress, 10, message: "Converting image...");
            encodeImageFormat(inputTmp, outputForFfmpeg);

            if (!needPostProcessing || postConverter == null) return; // file is already in final state, no post-processing needed

            progress[key] = new(FileConversionStatus.InProgress, 30, message: "Applying adjustments...");
            // post-processing:
            IImage img;
            using var stream = File.OpenRead(outputForFfmpeg);
            try {
                if (postConverter is ImageConverterBase imgConverter) {
                    img = imgConverter.Load(stream);
                } else {
                    img = NativeImage.Load(stream);
                }
            } finally {
                stream.Dispose();
            }
            img = img.Adjust(adj);
            progress[key] = new(FileConversionStatus.InProgress, 50, message: "Post format conversion...");

            if (postConverter.SupportsConversion(FileFormat.Png, adj.RequestedFormat)) {
                // use the post-processing and output correct format directly
                var bytes = img.Encode(adj.RequestedFormat, adj.Quality);
                progress[key] = new(FileConversionStatus.InProgress, 90, message: "Finalizing format conversion...");
                File.WriteAllBytes(outputTmp, bytes);
            } else if (this.SupportsConversion(FileFormat.Png, adj.RequestedFormat)) {
                // FFMpeg supports format so use this, (slow but FFMpeg has wide format support, for example AVIF )
                var bytes = img.Encode(FileFormat.Png, adj.Quality);
                progress[key] = new(FileConversionStatus.InProgress, 70, message: "Finalizing format to " + adj.RequestedFormat.ToString().ToUpper() + "...");
                encodeImageFormat(bytes, FileFormat.Png, outputTmp);
                progress[key] = new(FileConversionStatus.InProgress, 95, message: "Finalizing...");
            } else {
                throw new InvalidOperationException("Neither the post-processing converter nor ffmpeg supports the requested output format.");
            }
        } finally {
            if (needPostProcessing) tryDelete(outputForFfmpeg);
        }
    }

    async Task extractThumbnailAndProcessAsync(string inputTmp, string outputTmp, FileAdjustmentImage adj,
        Guid key, ConcurrentDictionary<Guid, FileConversionProgressInfo> progress, CancellationToken ct) {

        var needPostProcessing = // these are not supported by ffmpeg filters
            adj.Brightness.HasValue || adj.Contrast.HasValue ||
            adj.Saturation.HasValue || adj.Sharpness.HasValue || adj.HueShift.HasValue;

        IFileConverter? postConverter = null;
        if (needPostProcessing) {
            if (!tryGetConverter(FileFormat.Png, FileFormat.Png, out postConverter))
                throw new InvalidOperationException("Converter library does not support required format for status response generation.");
        }

        string outputForFfmpeg = needPostProcessing ? getTempPath(FileFormat.Png) : outputTmp;

        try {

            await extractThumbnailAsyncInner(inputTmp, outputForFfmpeg, adj, key, progress, ct);

            if (!needPostProcessing || postConverter == null) return; // file is already in final state, no post-processing needed

            // post-processing:
            IImage img;
            using var stream = File.OpenRead(outputForFfmpeg);
            try {
                if (postConverter is ImageConverterBase imgConverter) {
                    img = imgConverter.Load(stream);
                } else {
                    img = NativeImage.Load(stream);
                }
            } finally {
                stream.Dispose();
            }
            img = img.Adjust(adj);

            if (postConverter.SupportsConversion(FileFormat.Png, adj.RequestedFormat)) {
                // use the post-processing and output correct format directly
                var bytes = img.Encode(adj.RequestedFormat, adj.Quality);
                File.WriteAllBytes(outputTmp, bytes);
            } else if (this.SupportsConversion(FileFormat.Png, adj.RequestedFormat)) {
                // FFMpeg supports format so use this, (slow but FFMpeg has wide format support, for example AVIF )
                var bytes = img.Encode(FileFormat.Png, adj.Quality);
                encodeImageFormat(bytes, FileFormat.Png, outputTmp);
            } else {
                throw new InvalidOperationException("Neither the post-processing converter nor ffmpeg supports the requested output format.");
            }
        } finally {
            if (needPostProcessing) tryDelete(outputForFfmpeg);
        }
    }
    async Task extractThumbnailAsyncInner(string inputTmp, string outputTmp, FileAdjustmentImage adj,
    Guid key, ConcurrentDictionary<Guid, FileConversionProgressInfo> progress, CancellationToken ct) {
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
        progress[key] = new(FileConversionStatus.InProgress, 95, message: "Finalizing...");
    }

    static async Task<TimeSpan?> resolveSeekPosition(string inputTmp, FileAdjustmentImage? adj, CancellationToken ct) {
        if (adj?.TimeOffsetMs == -1) {
            // use "Smart "Representative" Thumbnail "
            var info = await FFProbe.AnalyseAsync(inputTmp, cancellationToken: ct);
            if (info.Duration > TimeSpan.Zero) {
                return info.Duration / 2;
            } else {
                return TimeSpan.Zero;
            }
        }
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
        return position;
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
        await processor.CancellableThrough(ct).ProcessAsynchronously();
        progress[key] = new(FileConversionStatus.InProgress, 95, message: "Finalizing...");
    }

    static void tryDelete(string path) {
        //Console.WriteLine("Trying to delete temp file: " + path);
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public bool TryGetLiveStatus(Guid key, [MaybeNullWhen(false)] out FileConversionProgressInfo status) {
        if (_conversionProgress.TryGetValue(key, out var liveProgress)) status = liveProgress;
        else if (!_ffmpegBinReady) status = new(FileConversionStatus.InProgress, 0, message: _ffmpegBinProgressInfo);
        else status = null;
        return status != null;
    }
    public byte[] CreateStatusResponse(FileFormat requestedFormat, int width, int height, List<string> text, string textColor, string fillColor) {
        var baseRequestFormat = FileFormatUtil.GetFileType(requestedFormat);
        if (baseRequestFormat == FileType.Video) {
            ensureFFMpegBinAsync().Wait(); // inefficient but this is just for status generation and ensures progress info is updated
            int vw = width % 2 == 0 ? width : width + 1; // video dimensions must be even for most codecs
            int vh = height % 2 == 0 ? height : height + 1; // video dimensions must be even for most codecs
            if (!tryGetConverter(FileFormat.Png, FileFormat.Png, out var converter))
                throw new InvalidOperationException("Converter library does not support required format for status response generation.");
            IImage img;
            if (converter is ImageConverterBase imgConverter) {
                img = imgConverter.Create(vw, vh).GetStatusImage(text, textColor, fillColor);
            } else {
                img = NativeImage.Create(vw, vh).GetStatusImage(text, textColor, fillColor);
            }
            var imgBytes = img.Encode(FileFormat.Png);
            var imgTmp = getTempPath(FileFormat.Png);
            var vidTmp = getTempPath(requestedFormat);
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
            if (!tryGetConverter(FileFormat.Png, FileFormat.Png, out var converter))
                throw new InvalidOperationException("Converter library does not support required format for status response generation.");
            IImage img;
            if (converter is ImageConverterBase imgConverter) {
                img = imgConverter.Create(width, height).GetStatusImage(text, textColor, fillColor);
            } else {
                img = NativeImage.Create(width, height).GetStatusImage(text, textColor, fillColor);
            }
            if (converter.SupportsConversion(FileFormat.Png, requestedFormat)) {
                return img.Encode(requestedFormat);
            } else {
                // final fallback is ffmpeg, slow but at least it will work for almost any format
                return encodeImageFormat(img.Encode(FileFormat.Png), FileFormat.Png, requestedFormat);
            }
        } else if (baseRequestFormat == FileType.Meta) {
            return new BasicFileMeta().ToBytes();
        } else {
            throw new ArgumentException("Requested format must be a video format for status response generation.");
        }
    }
    byte[] encodeImageFormat(byte[] data, FileFormat from, FileFormat to) {
        ensureFFMpegBinAsync().Wait();
        var outputTmp = getTempPath(to);
        try {
            encodeImageFormat(data, from, outputTmp);
            return File.ReadAllBytes(outputTmp);
        } finally {
            tryDelete(outputTmp);
        }
    }
    void encodeImageFormat(byte[] data, FileFormat from, string outputTmp) {
        ensureFFMpegBinAsync().Wait();
        if (FileFormatUtil.GetFileType(from) != FileType.Image)
            throw new ArgumentException("Format must be an image format.");
        var inputTmp = getTempPath(from);
        try {
            File.WriteAllBytes(inputTmp, data);
            encodeImageFormat(inputTmp, outputTmp);
        } finally {
            tryDelete(inputTmp);
        }
    }
    void encodeImageFormat(string inputTmp, string outputTmp) {
        ensureFFMpegBinAsync().Wait();
        try {
            FFMpegArguments
                .FromFileInput(inputTmp)
                .OutputToFile(outputTmp, true)
                .ProcessSynchronously();
        } catch {
            tryDelete(outputTmp);
        }
    }

}
