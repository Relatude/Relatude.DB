using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Files;
using Relatude.DB.FileConversion;
using Relatude.DB.FileToText;
using Relatude.DB.Web;
using System.Diagnostics.CodeAnalysis;
namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    /// <summary>
    /// The engine that converts this store's files, with the file converters the server was given.
    /// Also what converts files code holds that are not the store's own (<see cref="FileConversionEngine.ConvertFileAsync"/>),
    /// as the FileToText provider converts a video to the sound it sends.
    /// </summary>
    public FileConversionEngine FileConversion => _fileConversionEngine;
    public bool CanConvert(FileFormat from, FileFormat to) {
        return _fileConversionEngine.CanConvert(from, to);
    }
    public bool CanConvert(PropertyPath propertyPath, FileAdjustmentBase adj, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        if (fileValue.IsEmpty || fileValue.PropertyPath == null) {
            return false;
        }
        adj = resolveAdaptiveFormat(fileValue, adj, out var serveOriginal);
        if (serveOriginal) return true; // no conversion involved, the original is served as is
        return _fileConversionEngine.CanConvert(fileValue.Format, adj.RequestedFormat);
    }
    /// <summary>
    /// The adjustment a conversion is keyed, run and cached by. First normalized
    /// (<see cref="FileAdjustmentBase.Normalized"/>), so the same picture has one key whether it was asked
    /// for by an encoded URL, a readable one, the admin UI or application code - before, only the encoded
    /// URLs and the admin UI were sanitized, and the others converted and cached a second copy. Then the
    /// adaptive FileFormat.Image is resolved against the original file and the store defaults
    /// (SettingsLocal.ImageDefaultFormat / ImageDefaultQuality), so cache keys, converter selection
    /// and content types always see a concrete format. serveOriginal is true for a plain adaptive
    /// request with no adjustments at all, or for a gif at its own size: the original file is served untouched. Not
    /// for an OpenRaster file, which no browser shows: its merged image is converted to the default format.
    /// </summary>
    FileAdjustmentBase resolveAdaptiveFormat(FileValue fileValue, FileAdjustmentBase adj, out bool serveOriginal) {
        serveOriginal = false;
        adj = adj.Normalized(); // a copy: the caller's instance may be shared (the URL decoders cache theirs)
        // a transcript in no language named is the one the text analysis makes: one transcript, paid once
        if (adj is FileAdjustmentTranscript transcript) transcript.Language ??= FileAdjustmentTranscript.LanguageCodeOf(analysisLanguage());
        if (adj is FileAdjustmentImage asked) applyPictureDefaults(fileValue, asked);
        if (adj is not FileAdjustmentImage image || image.RequestedFormat != FileFormat.Image) return adj;
        if (image.IsPlainRequest() && fileValue.Format != FileFormat.Ora // a browser shows no OpenRaster file: its picture is made
            || image.KeepsOriginalGif(fileValue.Format, fileValue.Width, fileValue.Height)) {
            serveOriginal = true;
            return adj;
        }
        return image.ResolveAdaptiveFormat(fileValue.Format, fileValue.Width, fileValue.Height,
            _settings.ImageDefaultFormat.ToFileFormat(), _settings.ImageDefaultQuality);
    }
    /// <summary>
    /// The picture's own defaults (<see cref="ImageFileMeta.CropMode"/>, <see cref="ImageFileMeta.BackgroundColor"/>,
    /// <see cref="ImageFileMeta.FocusPoint"/>) put into an adjustment that names none, where they change what is made: the
    /// crop mode only for a size of two sides, the background only where the picture may be see-through (a format that
    /// holds transparency) or the canvas may be larger than it (Fit, Auto, or a turn that is no quarter), the focus point
    /// only where it places a crop (<see cref="usesFocus"/>), turned with the picture, as the engine reads a focus on the
    /// turned canvas. Part of the key, so a changed default makes new pictures, and part of the URL's version
    /// (<see cref="pictureDefaultsKey"/>), so browsers fetch them. The caller's instance is never changed: this is the
    /// normalized copy. A video's picture takes its frame's default instead (<see cref="applyFrameDefault"/>).
    /// </summary>
    static void applyPictureDefaults(FileValue fileValue, FileAdjustmentImage adj) {
        if (fileValue.FileType == FileType.Video) {
            applyFrameDefault(fileValue, adj);
            return;
        }
        if (pictureDefaultsOf(fileValue) is not { } meta) return;
        if (adj.CropMode == null && adj.Width != null && adj.Height != null && meta.CropMode is { } mode) adj.CropMode = mode;
        if (adj.BackgroundColor == null && adj.AutoBackgroundColor != true && IImageExt.IsVisibleColor(meta.BackgroundColor) && showsBackground(fileValue, adj)) {
            IImageExt.TryParseHexColor(meta.BackgroundColor, out var r, out var g, out var b, out var a);
            adj.BackgroundColor = a == 255 ? $"#{r:x2}{g:x2}{b:x2}" : $"#{r:x2}{g:x2}{b:x2}{a:x2}";
        }
        // after the crop mode, which decides whether the focus is used; each side the adjustment leaves out, as the engine reads them
        if (meta.FocusPoint is { } focus && (adj.FocusX == null || adj.FocusY == null) && usesFocus(fileValue, adj)
            && IImageExt.TurnedPoint(focus.X, focus.Y, fileValue.Width, fileValue.Height, adj.Rotation ?? 0) is { } turned) {
            adj.FocusX ??= (int)Math.Round(turned.X);
            adj.FocusY ??= (int)Math.Round(turned.Y);
        }
    }
    /// <summary>
    /// Whether an adjustment's focus places what is made (IImageExt.Adjust): the window of a zoom, or a crop to a size of two
    /// sides by Fill, or by Auto where it fills, that cuts more than a pixel of rounding. Not once a source rectangle is
    /// named, which the engine takes the focus from no more. So a picture made in its own shape, as the CMS's previews
    /// are, keeps its key when the focus point moves.
    /// </summary>
    static bool usesFocus(FileValue fileValue, FileAdjustmentImage adj) {
        if (adj.SourceWidth != null || adj.SourceHeight != null) return false;
        if (adj.Zoom is double zoom && zoom > 100) return true;
        if (adj.Width is not int width || adj.Height is not int height) return false;
        var mode = adj.CropMode ?? ImageCropMode.Fit;
        if (mode is not (ImageCropMode.Fill or ImageCropMode.Auto)) return false;
        if (fileValue.Width <= 0 || fileValue.Height <= 0) return true; // the picture's size not known: it may be cut
        var (w, h) = IImageExt.TurnedSize(fileValue.Width, fileValue.Height, adj.Rotation ?? 0);
        double pictureShape = (double)w / h, canvasShape = (double)width / height;
        if (mode == ImageCropMode.Auto && Math.Abs(pictureShape - canvasShape) / canvasShape < 0.05) return false; // fitted, as NativeImage.ResizeAuto
        var scale = Math.Max((double)width / w, (double)height / h);
        return Math.Round(w * scale) - width > 1 || Math.Round(h * scale) - height > 1;
    }
    static bool showsBackground(FileValue fileValue, FileAdjustmentImage adj) =>
        fileValue.Format != FileFormat.Svg // a drawing the browser draws: a background would have it converted
        && (fileValue.Format != FileFormat.Jpeg // every other picture format can hold transparency
        || adj.Width != null && adj.Height != null && adj.CropMode is null or ImageCropMode.Fit or ImageCropMode.Auto
        || adj.Rotation is double turn && turn % 90 != 0);
    /// <summary>The meta of a picture that has defaults of its own; the JSON is looked at before it is read, as most have none.</summary>
    static ImageFileMeta? pictureDefaultsOf(FileValue fileValue) {
        if (fileValue.IsEmpty || fileValue.FileType != FileType.Image) return null;
        var json = fileValue.MetaJSON;
        if (string.IsNullOrEmpty(json) || !json.Contains("\"CropMode\"", StringComparison.Ordinal) && !json.Contains("\"BackgroundColor\"", StringComparison.Ordinal)
            && !json.Contains("\"FocusPoint\"", StringComparison.Ordinal)) return null;
        return fileValue.GetMeta<ImageFileMeta>() is { } meta && (meta.CropMode != null || meta.BackgroundColor != null || meta.FocusPoint != null) ? meta : null;
    }
    /// <summary>
    /// A video's picture asked for at no time, and no series of frames: taken at its poster frame
    /// (<see cref="VideoFileMeta.PosterTime"/>), else <see cref="FileAdjustmentImage.DefaultTimeOffsetPercentage"/> in.
    /// Part of the key, so a poster frame moved makes new pictures, and of the URL's version.
    /// </summary>
    static void applyFrameDefault(FileValue fileValue, FileAdjustmentImage adj) {
        if (adj.Frames != null || adj.TimeOffsetMs != null || adj.TimeOffsetPercentage != null) return;
        if (posterTimeOf(fileValue) is { } time) adj.TimeOffsetMs = Math.Round(time.TotalMilliseconds);
        else adj.TimeOffsetPercentage = FileAdjustmentImage.DefaultTimeOffsetPercentage;
    }
    // the JSON is looked at before it is read, as most have none
    static TimeSpan? posterTimeOf(FileValue fileValue) {
        if (fileValue.IsEmpty || fileValue.FileType != FileType.Video) return null;
        var json = fileValue.MetaJSON;
        if (string.IsNullOrEmpty(json) || !json.Contains("\"PosterTime\"", StringComparison.Ordinal)) return null;
        return fileValue.GetMeta<VideoFileMeta>()?.PosterTime is { } time && time >= TimeSpan.Zero ? time : null;
    }
    /// <summary>What the picture's defaults put into an adjustment, for the URL's version: empty when they put in nothing.</summary>
    static string pictureDefaultsKey(FileValue fileValue, FileAdjustmentBase adj) {
        if (adj is FileAdjustmentImage frame && fileValue.FileType == FileType.Video) {
            var asked = (FileAdjustmentImage)frame.Normalized();
            if (asked.Frames != null || asked.TimeOffsetMs != null || asked.TimeOffsetPercentage != null) return string.Empty;
            applyFrameDefault(fileValue, asked);
            return asked.TimeOffsetMs is double ms ? "poster frame " + ms.ToString(System.Globalization.CultureInfo.InvariantCulture) : "default frame " + asked.TimeOffsetPercentage;
        }
        if (adj is not FileAdjustmentImage image || pictureDefaultsOf(fileValue) == null) return string.Empty;
        var resolved = (FileAdjustmentImage)image.Normalized();
        var (mode, background, focusX, focusY) = (resolved.CropMode, resolved.BackgroundColor, resolved.FocusX, resolved.FocusY);
        applyPictureDefaults(fileValue, resolved);
        if (resolved.CropMode == mode && resolved.BackgroundColor == background && resolved.FocusX == focusX && resolved.FocusY == focusY) return string.Empty;
        var defaults = "picture defaults " + resolved.CropMode + " " + resolved.BackgroundColor;
        // the focus only when it was put in: the version of a URL the focus does not change stays what it was
        return resolved.FocusX == focusX && resolved.FocusY == focusY ? defaults : defaults + " focus " + resolved.FocusX + "," + resolved.FocusY;
    }
    public async Task<Stream> GetFileStream(PropertyPath propertyPath, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        if (fileValue.IsEmpty) throw new Exception("File value is empty");
        var fileStore = getFileStore(fileValue.StorageId);
        return await fileStore.GetFileStream(fileValue);
    }
    public async Task<StreamAndValue> GetFileStreamAndValue(PropertyPath propertyPath, QueryContext? ctx = null) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        if (fileValue.IsEmpty) throw new Exception("File value is empty");
        var fileStore = getFileStore(fileValue.StorageId);
        var stream = await fileStore.GetFileStream(fileValue);
        return new StreamAndValue(stream, fileValue);
    }
    public async Task<Stream> GetFileStream(PropertyPath propertyPath, FileAdjustmentBase adj, int maxWait = -1, QueryContext? ctx = null, bool firstInQueue = false) {
        return (await GetFileStreamAndState(propertyPath, adj, maxWait, ctx, firstInQueue)).Stream;
    }
    public Task<StateAndStream> GetFileStreamAndState(PropertyPath propertyPath, FileAdjustmentBase adj, int maxWait = -1, QueryContext? ctx = null, bool firstInQueue = false)
        => getFileStreamAndStateAsync(propertyPath, adj, maxWait, ctx, firstInQueue, fromUrl: false);
    /// <summary>
    /// As <see cref="GetFileStreamAndState(PropertyPath, FileAdjustmentBase, int, QueryContext?, bool)"/>, for an adjustment a
    /// URL names, which anyone may ask for: it starts no conversion that costs credits. A transcript
    /// (<see cref="FileAdjustmentTranscript"/>) is served when it is made or being made, and otherwise answered with its
    /// status, not cacheable, nothing queued; code asking the store, or the text analysis, makes it.
    /// </summary>
    public Task<StateAndStream> GetFileStreamAndStateForUrl(PropertyPath propertyPath, FileAdjustmentBase adj, int maxWait = -1, QueryContext? ctx = null, bool firstInQueue = false)
        => getFileStreamAndStateAsync(propertyPath, adj, maxWait, ctx, firstInQueue, fromUrl: true);
    async Task<StateAndStream> getFileStreamAndStateAsync(PropertyPath propertyPath, FileAdjustmentBase adj, int maxWait, QueryContext? ctx, bool firstInQueue, bool fromUrl) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        adj = resolveAdaptiveFormat(fileValue, adj, out var serveOriginal);
        if (serveOriginal) { // a plain adaptive request: the original file, untouched
            var originalStore = getFileStore(fileValue.StorageId);
            var originalStream = await originalStore.GetFileStream(fileValue);
            return new StateAndStream(originalStream, true, fileValue, fileValue.Format, Guid.Empty, null);
        }
        var (info, source) = conversionOf(propertyPath, fileValue, adj);
        var conversionId = info.IdWithAdjustment.GetKey();
        if (adj is FileAdjustmentTranscript transcript) {
            await keepTranscriptMadeBeforeAsync(fileValue, info, source, transcript);
            if (fromUrl && transcriptNotStarted(info, source, transcript) is { } why) {
                var status = new MemoryStream(TranscriptStatus.Of(adj.RequestedFormat, why), writable: false);
                return new StateAndStream(status, false, fileValue, adj.RequestedFormat, conversionId, info);
            }
        }
        var result = await _fileConversionEngine.TryGetFormatAndStreamAsync(info, maxWait, source, firstInQueue);
        Stream stream;
        if (result.ProgressInfo.Status == FileConversionStatus.Ready) {
            if (result.Output == null) throw new Exception("File conversion output is null"); // should never happen, but just in case
            stream = result.Output;
        } else {
            stream = _fileConversionEngine.GetStatusDataStream(fileValue, adj, result.ProgressInfo);
        }
        return new StateAndStream(stream, result.ProgressInfo.Status == FileConversionStatus.Ready, fileValue, adj.RequestedFormat, conversionId, info);
    }
    public bool TryGetConversionInfo(PropertyPath propertyPath, FileAdjustmentBase adj, bool requestIfNot, [MaybeNullWhen(false)] out FileConversionProgressInfo progressInfo, QueryContext? ctx = null, bool firstInQueue = false) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        adj = resolveAdaptiveFormat(fileValue, adj, out var serveOriginal);
        if (serveOriginal) { // a plain adaptive request never converts, the original is always ready
            progressInfo = new FileConversionProgressInfo(FileConversionStatus.Ready, 100, 0);
            return true;
        }
        var (info, source) = conversionOf(propertyPath, fileValue, adj);
        if (adj is FileAdjustmentTranscript transcript && madeBefore(fileValue, info, source, transcript) is { } kept) {
            // rare (once a file, after its conversions were cleared), and a write of a small file: not worth an async version
            Task.Run(() => _fileConversionEngine.KeepAsync(info, transcript.AsJson(), kept.ToBytes())).GetAwaiter().GetResult();
        }
        return _fileConversionEngine.TryGetProgressInfo(info, requestIfNot, source, out progressInfo, firstInQueue);
    }

    /// <summary>A conversion of the file the property holds, as the engine keys, runs and caches it, and where its converter reads the file.</summary>
    (FileConversionInfo Info, InputFileSource Source) conversionOf(PropertyPath propertyPath, FileValue fileValue, FileAdjustmentBase adj) {
        var info = new FileConversionInfo(FileIdWithAdjustment.Of(fileValue, adj, propertyPath), fileValue.Name, fileValue.Hash, fileValue.Format);
        var fileStore = getFileStore(fileValue.StorageId);
        if (fileStore.TryGetLocalFilePath(fileValue, out var localFilePath)) {
            return (info, new InputFileSource(() => Task.FromResult<Stream>(File.OpenRead(localFilePath)), localFilePath));
        }
        return (info, new InputFileSource(async () => {
            if (fileValue.IsEmpty) throw new Exception("File value is empty");
            return await fileStore.GetFileStream(fileValue);
        }, null));
    }

    /// <summary>
    /// Why a URL may not have the transcript asked for: none made, none being made, or the last try failed. Null when it
    /// is made or being made, so the format asked for (written from it at no cost) may be.
    /// </summary>
    string? transcriptNotStarted(FileConversionInfo info, InputFileSource source, FileAdjustmentTranscript transcript) {
        var json = FileConversionEngine.InfoOf(info, transcript.AsJson());
        if (!_fileConversionEngine.TryGetProgressInfo(json, false, source, out var status)) return "No transcript of this file has been made. ";
        return status.Status == FileConversionStatus.Error ? "The transcript of this file could not be made: " + status.Message : null;
    }

    /// <summary>
    /// The transcript the text analysis keeps with the file (<see cref="FileValue.TextExtract"/>), when a transcript in the
    /// analysis's language is asked for and none is cached: a transcript made before transcripts were conversions, or
    /// one whose conversion was cleared since. Kept as the conversion at no cost, rather than paid for again.
    /// </summary>
    TranscriptData? madeBefore(FileValue fileValue, FileConversionInfo info, InputFileSource source, FileAdjustmentTranscript transcript) {
        if (string.IsNullOrEmpty(fileValue.TextExtract) || fileValue.FileType is not (FileType.Audio or FileType.Video)) return null;
        var meta = fileValue.GetMeta();
        if (meta.TextExtraction is not { Timed: true, Error: null } step) return null;
        if (transcript.Language != FileAdjustmentTranscript.LanguageCodeOf(analysisLanguage())) return null;
        var json = FileConversionEngine.InfoOf(info, transcript.AsJson());
        if (_fileConversionEngine.TryGetProgressInfo(json, false, source, out var status) && status.Status != FileConversionStatus.Error) return null; // made, or being made
        return new TranscriptData {
            Text = fileValue.TextExtract,
            Timed = true,
            Language = meta.Language,
            Duration = meta is MediaFileMeta { Duration: { } length } ? length.TotalSeconds : null,
            Characters = step.Characters > 0 ? step.Characters : fileValue.TextExtract.Length,
            Truncated = step.Truncated,
            Format = step.Format,
            Provider = step.Provider,
            MadeUtc = step.CompletedUtc,
        };
    }
    async Task keepTranscriptMadeBeforeAsync(FileValue fileValue, FileConversionInfo info, InputFileSource source, FileAdjustmentTranscript transcript) {
        if (madeBefore(fileValue, info, source, transcript) is { } kept) await _fileConversionEngine.KeepAsync(info, transcript.AsJson(), kept.ToBytes());
    }

    /// <summary>
    /// Every format of a file's transcript in one language taken out of the cache, its failures too: what a transcript
    /// made again (text extraction with force) does first, so it is made anew while the speech it was made of stays kept.
    /// </summary>
    void clearTranscripts(FileConversionInfo info, FileAdjustmentTranscript transcript) {
        FileAdjustmentTranscript[] formats = [
            transcript.AsJson(),
            new() { RequestedFormat = FileFormat.Txt, Language = transcript.Language },
            new() { RequestedFormat = FileFormat.Txt, Language = transcript.Language, Timestamps = false },
            new() { RequestedFormat = FileFormat.Vtt, Language = transcript.Language },
            new() { RequestedFormat = FileFormat.Srt, Language = transcript.Language },
        ];
        foreach (var format in formats) _fileConversionEngine.ClearCache(FileConversionEngine.InfoOf(info, format).IdWithAdjustment);
    }
    public bool IsFileReady(PropertyPath propertyPath, FileAdjustmentBase adj, bool requestIfNot, QueryContext? ctx = null, bool firstInQueue = false) {
        return TryGetConversionInfo(propertyPath, adj, requestIfNot, out var progressInfo, ctx, firstInQueue) && progressInfo.Status != FileConversionStatus.InProgress;
    }
    public void EnsureConversionRequested(PropertyPath propertyPath, FileAdjustmentBase adj, QueryContext? ctx = null, bool firstInQueue = false) {
        TryGetConversionInfo(propertyPath, adj, true, out _, ctx, firstInQueue);
    }
    public FileConversions GetConversions(QueryContext? ctx = null) => _fileConversionEngine.GetConversions();
    public async Task CancelAllConversions(bool permanently, QueryContext? ctx = null) {
        foreach (var conversion in _fileConversionEngine.GetConversions().Current) {
            if (conversion.Status == ConversionStatus.Running || conversion.Status == ConversionStatus.Queued) {
                await _fileConversionEngine.CancelRunning(conversion.Id, permanently);
            }
        }
    }
    public Task CancelConversion(Guid conversionId, bool permanently, QueryContext? ctx = null) => _fileConversionEngine.CancelRunning(conversionId, permanently);
    public void ClearAllCachedConversions(QueryContext? ctx = null) => _fileConversionEngine.ClearAllCache();
    public void ClearAllCachedConversionsErrors(QueryContext? ctx = null) => _fileConversionEngine.ClearAllErrors();
}
