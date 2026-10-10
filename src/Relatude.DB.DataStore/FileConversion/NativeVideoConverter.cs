using Relatude.DB.Common;
using Relatude.DB.FileConversion.FFMpeg;
using Relatude.DB.FileConversion.ImageEncoders;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Relatude.DB.FileConversion;

public class NativeVideoConverter : IFileConverter {
    public NativeVideoConverter(int? threadCount = null) {
        ThreadCount = threadCount.HasValue ? threadCount.Value : Math.Max(1, Environment.ProcessorCount / 8);
        //ThreadCount = threadCount.HasValue ? threadCount.Value : 1;
    }
    public int ThreadCount { get; set; }
    public int CallDelayMs { get; set; } = 0;

    static readonly FileFormat[] _videoIns = [FileFormat.Mp4, FileFormat.Avi, FileFormat.Mov, FileFormat.Wmv, FileFormat.Flv, FileFormat.Mkv, FileFormat.Webm];
    static readonly FileFormat[] _videoOuts = [FileFormat.Mp4, FileFormat.Avi, FileFormat.Mov, FileFormat.Wmv, FileFormat.Mkv];
    static readonly FileFormat[] _audioIns = [FileFormat.Mp3, FileFormat.Wav, FileFormat.Aac, FileFormat.Flac, FileFormat.Ogg, FileFormat.M4a];
    static readonly FileFormat[] _audioOuts = [FileFormat.Mp3, FileFormat.Wav, FileFormat.Aac, FileFormat.Flac, FileFormat.Ogg, FileFormat.M4a];
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
            if (outBase == FileType.Audio) return _audioOuts.Contains(outDetailed); // supported video to audio (its sound)
        }
        if (inBase == FileType.Audio) {
            if (!_audioIns.Contains(inDetailed)) return false; // unsupported input audio format
            if (outBase == FileType.Audio) return _audioOuts.Contains(outDetailed); // supported audio to audio
            if (outBase == FileType.Meta) return _metaOuts.Contains(outDetailed); // supported audio to meta (its length)
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
    string getTempPath(FileFormat format) {
        Directory.CreateDirectory(engine.LocalTempFolderPath); // ffmpeg does not create missing output folders
        return Path.Combine(engine.LocalTempFolderPath, $"{Guid.NewGuid()}{FileFormatUtil.GetExtensionWithDot(format)}");
    }

    static async Task ensureFFMpegBinAsync() {
        if (_ffmpegBinReady) return;
        await _downloadLock.WaitAsync();
        try {
            if (_ffmpegBinReady) return;
            FFmpegProcess.BinaryFolder = _ffmpegBinDir; // its binaries when there, else the ones on the PATH
            Directory.CreateDirectory(_ffmpegBinDir);
            _ffmpegBinProgressInfo = "Downloading FFmpeg...";
            await FFmpegBinaryDownloader.EnsureAsync(_ffmpegBinDir,
                (name, downloadedBytes, totalBytes) => _ffmpegBinProgressInfo = totalBytes > 0
                    ? $"Downloading {name}: {downloadedBytes / 1024} KB / {totalBytes / 1024} KB"
                    : $"Downloading {name}: {downloadedBytes / 1024} KB");
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
                    await extractThumbnailAndProcessAsync(inputFilePath, outputFilePath, info, imgAdj, key, _conversionProgress, cts.Token);
                } else if (typeTo == FileType.Video) { // video to video conversion
                    await convertVideoAsync(inputFilePath, outputFilePath, info, key, _conversionProgress, cts.Token);
                } else if (typeTo == FileType.Meta) { // video to video conversion
                    await extractMetaAsync(inputFilePath, outputFilePath, cts.Token);
                } else if (typeTo == FileType.Audio) { // video to audio: its sound
                    await extractAudioAsync(inputFilePath, outputFilePath, info, key, _conversionProgress, cts.Token);
                } else {
                    throw new NotSupportedException("Unsupported output file type: " + typeTo);
                }
            } else if (typeFrom == FileType.Audio) {
                if (typeTo == FileType.Audio) { // audio to audio
                    await extractAudioAsync(inputFilePath, outputFilePath, info, key, _conversionProgress, cts.Token);
                } else if (typeTo == FileType.Meta) { // audio to meta: its length
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
    /// <summary>
    /// The sound of a video or a recording, in the requested audio format, as the audio adjustment asks
    /// (<see cref="FileAdjustmentAudio"/>): its first sound stream only - no pictures, subtitles,
    /// chapters or metadata - from <see cref="FileAdjustmentAudio.StartMs"/> for
    /// <see cref="FileAdjustmentAudio.DurationMs"/> when they are set. Written bit-exact, so the same
    /// file and adjustment give the same bytes on every run: a service that keeps files by their hash,
    /// such as the Relatude FileToText service, then knows the sound of a video it has read before.
    /// A file without sound is an error that says so.
    /// </summary>
    async Task extractAudioAsync(string inputTmp, string outputTmp, FileConversionInfo info, Guid key,
        ConcurrentDictionary<Guid, FileConversionProgressInfo> progress, CancellationToken ct) {
        var adj = info.IdWithAdjustment.Adjustment as FileAdjustmentAudio ?? new FileAdjustmentAudio { RequestedFormat = info.Formats.To };
        progress[key] = new(FileConversionStatus.InProgress, 5, message: "Analyzing input...");
        var probe = await FFmpegProcess.ProbeAsync(inputTmp, ct);
        if (!probe.HasAudio) throw new InvalidOperationException("The file has no sound. ");
        var (codec, muxer, lossy) = audioEncoder(info.Formats.To);
        var start = adj.StartMs is { } startMs ? TimeSpan.FromMilliseconds(startMs) : TimeSpan.Zero;
        var length = probe.Duration - start;
        if (adj.DurationMs is { } durationMs && TimeSpan.FromMilliseconds(durationMs) < length) length = TimeSpan.FromMilliseconds(durationMs);
        // Opus encodes at 8, 12, 16, 24 or 48 kHz only
        var sampleRate = adj.SampleRate is { } rate && info.Formats.To == FileFormat.Ogg ? _opusRates.MinBy(r => Math.Abs(r - rate)) : adj.SampleRate;
        var sw = Stopwatch.StartNew();
        progress[key] = new(FileConversionStatus.InProgress, 10, message: "Converting...");
        List<string> args = [];
        if (start > TimeSpan.Zero) args.AddRange(["-ss", seconds(start)]);
        args.AddRange(["-i", inputTmp, "-map", "0:a:0", "-vn", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1"]);
        if (adj.DurationMs is { } duration) args.AddRange(["-t", seconds(TimeSpan.FromMilliseconds(duration))]);
        if (adj.Channels is { } channels) args.AddRange(["-ac", channels.ToString(CultureInfo.InvariantCulture)]);
        if (sampleRate is { } samples) args.AddRange(["-ar", samples.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(["-c:a", codec]);
        if (lossy && adj.BitRateKbps is { } kbps) args.AddRange(["-b:a", kbps.ToString(CultureInfo.InvariantCulture) + "k"]);
        if (adj.Speech && info.Formats.To == FileFormat.Ogg) args.AddRange(["-application", "voip"]);
        // no encoder version, creation time or random stream serial: the same bytes every run
        args.AddRange(["-fflags", "+bitexact", "-flags:a", "+bitexact"]);
        args.AddRange(["-f", muxer, outputTmp]);
        await FFmpegProcess.RunAsync(args, ct, length > TimeSpan.Zero ? length : probe.Duration, percent => {
            var remaining = percent > 2 ? (int)Math.Round((100 - percent) / (percent / sw.Elapsed.TotalSeconds)) : 0;
            progress[key] = new(FileConversionStatus.InProgress, (int)Math.Round(percent), remaining, message: "Converting...");
        });
        progress[key] = new(FileConversionStatus.InProgress, 95, message: "Finalizing...");
    }
    static readonly int[] _opusRates = [8_000, 12_000, 16_000, 24_000, 48_000];
    /// <summary>The encoder and the container ffmpeg writes an audio format with, and whether a bit rate applies.</summary>
    static (string Codec, string Muxer, bool Lossy) audioEncoder(FileFormat format) => format switch {
        FileFormat.Mp3 => ("libmp3lame", "mp3", true),
        FileFormat.Ogg => ("libopus", "ogg", true),
        FileFormat.Aac => ("aac", "adts", true),
        FileFormat.M4a => ("aac", "ipod", true),
        FileFormat.Flac => ("flac", "flac", false),
        FileFormat.Wav => ("pcm_s16le", "wav", false),
        _ => throw new NotSupportedException("Unsupported audio format: " + format),
    };
    static string seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
    static string scale(int width, int height) => string.Create(CultureInfo.InvariantCulture, $"scale={width}:{height}");
    async Task extractMetaAsync(string inputTmp, string outputTmp, CancellationToken ct) {
        var probe = await FFmpegProcess.ProbeAsync(inputTmp, ct);
        var meta = metaFromProbe(probe);
        var folder = Path.GetDirectoryName(outputTmp);
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder!);
        File.WriteAllBytes(outputTmp, meta.ToBytes());
    }
    static BasicFileMeta metaFromProbe(MediaProbe probe) {
        return new BasicFileMeta {
            Width = probe.Width,
            Height = probe.Height,
            Duration = probe.Duration,
            FormatDetails = probe.FormatLongName,
            AllMetaJson = probe.Json
        };
    }
    async Task processImageAsync(string inputTmp, string outputTmp, FileAdjustmentImage adj,
    Guid key, ConcurrentDictionary<Guid, FileConversionProgressInfo> progress, CancellationToken ct) {

        var needPostProcessing = // these are not supported by ffmpeg filters
            adj.Brightness.HasValue || adj.Contrast.HasValue ||
            adj.Saturation.HasValue || adj.Sharpness.HasValue || adj.HueShift.HasValue ||
            adj.InvertLuminance == true ||
            (adj.AutoLightDarkMode.HasValue && adj.AutoLightDarkMode != AutoLightDarkSwitch.None);

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
                var folder = Path.GetDirectoryName(outputTmp);
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder!);
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

    async Task extractThumbnailAndProcessAsync(string inputTmp, string outputTmp, FileConversionInfo info, FileAdjustmentImage adj,
        Guid key, ConcurrentDictionary<Guid, FileConversionProgressInfo> progress, CancellationToken ct) {

        var needPostProcessing = // these are not supported by ffmpeg filters
            adj.Brightness.HasValue || adj.Contrast.HasValue ||
            adj.Saturation.HasValue || adj.Sharpness.HasValue || adj.HueShift.HasValue ||
            adj.InvertLuminance == true ||
            (adj.AutoLightDarkMode.HasValue && adj.AutoLightDarkMode != AutoLightDarkSwitch.None);

        IFileConverter? postConverter = null;
        if (needPostProcessing) {
            if (!tryGetConverter(FileFormat.Png, FileFormat.Png, out postConverter))
                throw new InvalidOperationException("Converter library does not support required format for status response generation.");
        }

        string outputForFfmpeg = needPostProcessing ? getTempPath(FileFormat.Png) : outputTmp;

        try {

            await extractThumbnailAsyncInner(inputTmp, outputForFfmpeg, info, adj, key, progress, ct);

            if (!needPostProcessing || postConverter == null) return; // file is already in final state, no post-processing needed

            // post-processing:
            IImage img;
            var folder = Path.GetDirectoryName(outputForFfmpeg);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder!);
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
                var folder2 = Path.GetDirectoryName(outputTmp);
                if (!Directory.Exists(folder2)) Directory.CreateDirectory(folder2!);
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
    async Task extractThumbnailAsyncInner(string inputTmp, string outputTmp, FileConversionInfo info, FileAdjustmentImage adj,
    Guid key, ConcurrentDictionary<Guid, FileConversionProgressInfo> progress, CancellationToken ct) {
        int? w = adj?.Width, h = adj?.Height;
        progress[key] = new(FileConversionStatus.InProgress, 10, message: "Seeking to frame...");
        TimeSpan? seekTo = await resolveSeekPosition(inputTmp, info, adj, ct);
        progress[key] = new(FileConversionStatus.InProgress, 50, message: "Extracting frame...");
        var folder = Path.GetDirectoryName(outputTmp);
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder!);
        List<string> args = [];
        if (seekTo.HasValue) args.AddRange(["-ss", seconds(seekTo.Value)]);
        args.AddRange(["-i", inputTmp, "-frames:v", "1"]);
        if (w.HasValue || h.HasValue) args.AddRange(["-vf", scale(w ?? -1, h ?? -1)]);
        args.Add(outputTmp);
        await FFmpegProcess.RunAsync(args, ct);
        progress[key] = new(FileConversionStatus.InProgress, 95, message: "Finalizing...");
    }

    async Task<TimeSpan?> resolveSeekPosition(string inputTmp, FileConversionInfo info, FileAdjustmentImage? adj, CancellationToken ct) {
        var probe = await probeAndUpdateMeta(inputTmp, info, ct);
        if (adj?.TimeOffsetMs == -1) {
            // use "Smart "Representative" Thumbnail "
            if (probe.Duration > TimeSpan.Zero) {
                return probe.Duration / 2;
            } else {
                return TimeSpan.Zero;
            }
        }
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
    async Task<MediaProbe> probeAndUpdateMeta(string inputTmp, FileConversionInfo info, CancellationToken ct) {
        var probe = await FFmpegProcess.ProbeAsync(inputTmp, ct);
        if (_engine != null) {
            _engine!.Store.UpdateFileMetaIfNotSet(info.IdWithAdjustment.PropertyPath, info.IdWithAdjustment.FileId, metaFromProbe(probe));
        }
        return probe;
    }
    async Task convertVideoAsync(string inputTmp, string outputTmp, FileConversionInfo info,
        Guid key, ConcurrentDictionary<Guid, FileConversionProgressInfo> progress, CancellationToken ct) {
        var adj = info.IdWithAdjustment.Adjustment as FileAdjustmentVideo;
        progress[key] = new(FileConversionStatus.InProgress, 5, message: "Analyzing input...");
        var probe = await probeAndUpdateMeta(inputTmp, info, ct);
        var videoDuration = probe.Duration;
        var sw = Stopwatch.StartNew();
        progress[key] = new(FileConversionStatus.InProgress, 10, message: "Converting...");
        List<string> args = ["-i", inputTmp];
        // most video encoders take even sizes only: a size asked for is rounded up, and -2 keeps the other side even
        if (adj?.Width.HasValue == true || adj?.Height.HasValue == true)
            args.AddRange(["-vf", scale(adj.Width is { } width ? width + width % 2 : -2, adj.Height is { } height ? height + height % 2 : -2)]);
        if (adj?.TargetBitRateInMbps > 0)
            args.AddRange(["-b:v", ((int)(adj.TargetBitRateInMbps * 1024)).ToString(CultureInfo.InvariantCulture) + "k"]);
        args.Add(outputTmp);
        await FFmpegProcess.RunAsync(args, ct, videoDuration, progressInPercentage => {
            double progressPerSec = progressInPercentage / sw.Elapsed.TotalSeconds;
            double remainingSecs = (100 - progressInPercentage) / progressPerSec;
            int progressToReport = (int)Math.Round(progressInPercentage);
            int remainingSecsToReport = progressToReport > 2 ? (int)Math.Round(remainingSecs) : 0;
            progress[key] = new(FileConversionStatus.InProgress, progressToReport, remainingSecsToReport, message: "Converting...");
        });
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
                var folder = Path.GetDirectoryName(imgTmp);
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder!);
                File.WriteAllBytes(imgTmp, imgBytes);
                FFmpegProcess.Run(["-loop", "1", "-i", imgTmp, "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", vidTmp]);
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
        } else if (baseRequestFormat == FileType.Audio) {
            // a second of silence, for there is no text to show: a player has something to play, and asks again
            ensureFFMpegBinAsync().Wait();
            var audioTmp = getTempPath(requestedFormat);
            try {
                var (codec, muxer, _) = audioEncoder(requestedFormat);
                FFmpegProcess.Run(["-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-t", "1", "-c:a", codec, "-fflags", "+bitexact", "-f", muxer, audioTmp]);
                return File.ReadAllBytes(audioTmp);
            } finally {
                tryDelete(audioTmp);
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
            FFmpegProcess.Run(["-i", inputTmp, outputTmp]);
        } catch {
            tryDelete(outputTmp);
            throw;
        }
    }

}
