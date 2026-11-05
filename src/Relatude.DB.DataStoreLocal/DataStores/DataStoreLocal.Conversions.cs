using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.DataStores.Files;
using Relatude.DB.FileConversion;
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
    /// request with no adjustments at all, or for a gif at its own size: the original file is served untouched.
    /// </summary>
    FileAdjustmentBase resolveAdaptiveFormat(FileValue fileValue, FileAdjustmentBase adj, out bool serveOriginal) {
        serveOriginal = false;
        adj = adj.Normalized(); // a copy: the caller's instance may be shared (the URL decoders cache theirs)
        if (adj is FileAdjustmentImage asked) applyPictureDefaults(fileValue, asked);
        if (adj is not FileAdjustmentImage image || image.RequestedFormat != FileFormat.Image) return adj;
        if (image.IsPlainRequest() || image.KeepsOriginalGif(fileValue.Format, fileValue.Width, fileValue.Height)) {
            serveOriginal = true;
            return adj;
        }
        return image.ResolveAdaptiveFormat(fileValue.Format, fileValue.Width, fileValue.Height,
            _settings.ImageDefaultFormat.ToFileFormat(), _settings.ImageDefaultQuality);
    }
    /// <summary>
    /// The picture's own defaults (<see cref="ImageFileMeta.CropMode"/>, <see cref="ImageFileMeta.BackgroundColor"/>) put
    /// into an adjustment that names none, where they change what is made: the crop mode only for a size of two sides,
    /// the background only where the picture may be see-through (a format that holds transparency) or the canvas may be
    /// larger than it (Fit, Auto, or a turn that is no quarter). Part of the key, so a changed default makes new pictures,
    /// and part of the URL's version (<see cref="pictureDefaultsKey"/>), so browsers fetch them. The caller's instance
    /// is never changed: this is the normalized copy.
    /// </summary>
    static void applyPictureDefaults(FileValue fileValue, FileAdjustmentImage adj) {
        if (pictureDefaultsOf(fileValue) is not { } meta) return;
        if (adj.CropMode == null && adj.Width != null && adj.Height != null && meta.CropMode is { } mode) adj.CropMode = mode;
        if (adj.BackgroundColor == null && adj.AutoBackgroundColor != true && IImageExt.IsVisibleColor(meta.BackgroundColor) && showsBackground(fileValue, adj)) {
            IImageExt.TryParseHexColor(meta.BackgroundColor, out var r, out var g, out var b, out var a);
            adj.BackgroundColor = a == 255 ? $"#{r:x2}{g:x2}{b:x2}" : $"#{r:x2}{g:x2}{b:x2}{a:x2}";
        }
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
        if (string.IsNullOrEmpty(json) || !json.Contains("\"CropMode\"", StringComparison.Ordinal) && !json.Contains("\"BackgroundColor\"", StringComparison.Ordinal)) return null;
        return fileValue.GetMeta<ImageFileMeta>() is { } meta && (meta.CropMode != null || meta.BackgroundColor != null) ? meta : null;
    }
    /// <summary>What the picture's defaults put into an adjustment, for the URL's version: empty when they put in nothing.</summary>
    static string pictureDefaultsKey(FileValue fileValue, FileAdjustmentBase adj) {
        if (adj is not FileAdjustmentImage image || pictureDefaultsOf(fileValue) == null) return string.Empty;
        var resolved = (FileAdjustmentImage)image.Normalized();
        var (mode, background) = (resolved.CropMode, resolved.BackgroundColor);
        applyPictureDefaults(fileValue, resolved);
        return resolved.CropMode == mode && resolved.BackgroundColor == background ? string.Empty : "picture defaults " + resolved.CropMode + " " + resolved.BackgroundColor;
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
    public async Task<StateAndStream> GetFileStreamAndState(PropertyPath propertyPath, FileAdjustmentBase adj, int maxWait = -1, QueryContext? ctx = null, bool firstInQueue = false) {
        var fileValue = GetValue<FileValue>(propertyPath, ctx);
        adj = resolveAdaptiveFormat(fileValue, adj, out var serveOriginal);
        if (serveOriginal) { // a plain adaptive request: the original file, untouched
            var originalStore = getFileStore(fileValue.StorageId);
            var originalStream = await originalStore.GetFileStream(fileValue);
            return new StateAndStream(originalStream, true, fileValue, fileValue.Format, Guid.Empty, null);
        }
        var idWithAdj = FileIdWithAdjustment.Of(fileValue, adj, propertyPath);
        var info = new FileConversionInfo(idWithAdj, fileValue.Name, fileValue.Hash, fileValue.Format);
        var fileStore = getFileStore(fileValue.StorageId);
        InputFileSource source;
        var conversionId = idWithAdj.GetKey();
        if (fileStore.TryGetLocalFilePath(fileValue, out var localFilePath)) {
            var getInputStreamFunc = new Func<Task<Stream>>(async () => {
                return File.OpenRead(localFilePath);
            });
            source = new InputFileSource(getInputStreamFunc, localFilePath);
        } else {
            var getInputStreamFunc = new Func<Task<Stream>>(async () => {
                if (fileValue.IsEmpty) throw new Exception("File value is empty");
                return await fileStore.GetFileStream(fileValue);
            });
            source = new InputFileSource(getInputStreamFunc, null);
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
        var idWithAdj = FileIdWithAdjustment.Of(fileValue, adj, propertyPath);
        var fileStore = getFileStore(fileValue.StorageId);
        var fileConversionInfo = new FileConversionInfo(idWithAdj, fileValue.Name, fileValue.Hash, fileValue.Format);
        fileStore.TryGetLocalFilePath(fileValue, out var localFilePath);
        return _fileConversionEngine.TryGetProgressInfo(fileConversionInfo, requestIfNot, new InputFileSource(() => fileStore.GetFileStream(fileValue), localFilePath), out progressInfo, firstInQueue);
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
