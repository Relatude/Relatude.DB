using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.FileConversion;
using Relatude.DB.FileToText;
using Relatude.DB.Imaging;
using Relatude.DB.Tasks.FileAnalysis;
using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores;

public sealed partial class DataStoreLocal : IDataStore {
    IImagingProvider? _analysisImaging;
    IFileToTextProvider? _analysisFileToText;
    static readonly TimeSpan _serviceInfoMaxAge = TimeSpan.FromHours(1);
    (DateTime ReadUtc, ImagingOperations Operations)? _imagingOperations;
    (DateTime ReadUtc, FileToTextFormats Formats)? _fileToTextFormats;
    const string imageToMetaOperation = "image-to-meta";

    public void UseFileAnalysisProviders(IImagingProvider? imaging, IFileToTextProvider? fileToText) {
        _analysisImaging = imaging;
        _analysisFileToText = fileToText;
        _imagingOperations = null;
        _fileToTextFormats = null;
    }

    public FileAnalysisKinds GetWantedFileAnalyses(FilePropertyModel property, FileValue value) {
        if (value.IsEmpty) return FileAnalysisKinds.None;
        var kinds = FileAnalysisKinds.None;
        var fileType = value.FileType;
        if (fileType == FileType.Image && (property.AiImageAnalysis ?? _settings.EnableAiImageAnalysisByDefault)) kinds |= FileAnalysisKinds.AiImage;
        var textWanted = fileType is FileType.Audio or FileType.Video
            ? property.VideoAndAudioFileTextIndexing ?? _settings.EnableVideoAndAudioFileTextIndexingByDefault
            : property.FileTextIndexing ?? _settings.EnableFileTextIndexingByDefault;
        if (textWanted) kinds |= FileAnalysisKinds.Text;
        return kinds;
    }
    public static FileAnalysisKinds GetCompletedFileAnalyses(FileValue value) {
        if (!FileValueMeta.IsTyped(value.MetaJSON)) return FileAnalysisKinds.None;
        var meta = value.GetMeta();
        var kinds = FileAnalysisKinds.None;
        if (meta is ImageFileMeta { AiAnalysis: not null }) kinds |= FileAnalysisKinds.AiImage;
        if (meta.TextExtraction != null) kinds |= FileAnalysisKinds.Text;
        return kinds;
    }
    FileAnalysisKinds missingFileAnalyses(FilePropertyModel property, FileValue value) {
        var missing = GetWantedFileAnalyses(property, value) & ~GetCompletedFileAnalyses(value);
        if (_analysisImaging == null) missing &= ~FileAnalysisKinds.AiImage;
        if (_analysisFileToText == null) missing &= ~FileAnalysisKinds.Text;
        return missing;
    }
    bool anyFileAnalysisPossible() {
        if (_analysisImaging == null && _analysisFileToText == null) return false;
        if (_settings.EnableAiImageAnalysisByDefault || _settings.EnableFileTextIndexingByDefault || _settings.EnableVideoAndAudioFileTextIndexingByDefault) return true;
        return Datamodel.Properties.Values.OfType<FilePropertyModel>().Any(p => p.AiImageAnalysis == true || p.FileTextIndexing == true || p.VideoAndAudioFileTextIndexing == true);
    }

    bool queueFileAnalysisIfMissing(FileValue value) {
        if (value.IsEmpty || value.PropertyPath == null) return false;
        if (!Datamodel.Properties.TryGetValue(value.PropertyPath.PropertyId, out var property) || property is not FilePropertyModel fileProperty) return false;
        var missing = missingFileAnalyses(fileProperty, value);
        if (missing == FileAnalysisKinds.None) return false;
        EnqueueTask(new FileAnalysisTask(value.PropertyPath, value.FileId, missing));
        return true;
    }

    public int QueueMissingFileAnalyses(IEnumerable<Guid>? nodeTypeIds = null) {
        if (!anyFileAnalysisPossible()) return 0;
        int[] ids;
        _lock.EnterReadLock();
        try {
            validateDatabaseState();
            var withFiles = GetNodeTypeIdsThatMayContainFiles();
            if (nodeTypeIds != null) withFiles.IntersectWith(nodeTypeIds);
            ids = [.. withFiles.SelectMany(typeId => _definition.GetAllIdsForTypeNoAccessControl(typeId, false).ToArray())];
        } finally {
            _lock.ExitReadLock();
        }
        var queued = 0;
        var fileValues = new List<FileValue>();
        const int batchSize = 1000;
        for (var offset = 0; offset < ids.Length; offset += batchSize) {
            fileValues.Clear();
            var end = Math.Min(offset + batchSize, ids.Length);
            _lock.EnterReadLock();
            try {
                for (var i = offset; i < end; i++) {
                    if (_nodes.TryGet(ids[i], out var node, out _)) collectFileValues(node, fileValues);
                }
            } finally {
                _lock.ExitReadLock();
            }
            foreach (var fileValue in fileValues.DistinctBy(v => (v.PropertyPath, v.FileId))) {
                if (queueFileAnalysisIfMissing(fileValue)) queued++;
            }
        }
        if (queued > 0) LogInfo("Queued " + queued.To1000N() + " file" + (queued == 1 ? "" : "s") + " for analysis. ");
        return queued;
    }

    internal async Task<FileAnalysisOutcome> AnalyseFileAsync(FileAnalysisTask task, CancellationToken cancellationToken) {
        validateDatabaseState();
        var ctx = _defaultQueryCtx;
        if (!TryGetValue<FileValue>(task.PropertyPath, out var value, ctx) || value.IsEmpty || value.FileId != task.FileId) return new(true, "The file was replaced or removed before it was analysed. ");
        if (!Datamodel.Properties.TryGetValue(task.PropertyPath.PropertyId, out var property) || property is not FilePropertyModel fileProperty) return new(true, "The property is no longer a file property. ");
        var kinds = task.Kinds & missingFileAnalyses(fileProperty, value);
        if (kinds == FileAnalysisKinds.None) return new(true, "Nothing left to analyse. ");

        var report = new List<string>();
        var failed = false;
        ImageAiAnalysis? aiStep = null;
        ImagingMeta? aiMeta = null;
        if (kinds.HasFlag(FileAnalysisKinds.AiImage)) {
            try {
                (aiStep, aiMeta) = await analyseImageAsync(value, cancellationToken);
                report.Add(aiStep.Succeeded ? "AI image analysis: " + (aiMeta?.Keywords.Length ?? 0) + " keywords" : "AI image analysis skipped: " + aiStep.Error);
            } catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) {
                failed = true;
                report.Add("AI image analysis failed: " + error.Message);
            }
        }
        FileTextExtraction? textStep = null;
        FileToTextResult? text = null;
        if (kinds.HasFlag(FileAnalysisKinds.Text)) {
            try {
                (textStep, text) = await extractTextAsync(value, cancellationToken);
                report.Add(textStep.Succeeded ? "text: " + textStep.Characters.To1000N() + " characters" : "text extraction skipped: " + textStep.Error);
            } catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) {
                failed = true;
                report.Add("text extraction failed: " + error.Message);
            }
        }
        if (aiStep != null || textStep != null) {
            var stored = await storeFileAnalysisAsync(task, aiStep, aiMeta, textStep, text);
            if (!stored) report.Add("the file was replaced before the result could be stored");
        }
        return new(!failed, string.Join("; ", report) + ". ");
    }

    async Task<bool> storeFileAnalysisAsync(FileAnalysisTask task, ImageAiAnalysis? aiStep, ImagingMeta? aiMeta, FileTextExtraction? textStep, FileToTextResult? text) {
        var ctx = _defaultQueryCtx;
        var nodeId = _guids.ValidateAndReturnIntId(task.PropertyPath.NodePath.NodeKey);
        var lockId = await RequestLockAsync(nodeId, 10_000, 30_000);
        try {
            if (!TryGetValue<FileValue>(task.PropertyPath, out var current, ctx) || current.IsEmpty || current.FileId != task.FileId) return false;
            var updated = current.Copy();
            var meta = updated.GetMeta().AsKindFor(updated.FileType);
            if (aiStep != null) applyImageAnalysis(meta, aiStep, aiMeta);
            if (textStep != null) {
                meta.TextExtraction = textStep;
                if (text != null) applyText(updated, meta, text);
            }
            updated.SetMeta(meta);
            var t = new TransactionData { LockExcemptions = [lockId] };
            t.ForceUpdateProperty(task.PropertyPath, updated);
            Execute(t, false, false, ctx);
            return true;
        } finally {
            ReleaseLock(lockId);
        }
    }
    static void applyImageAnalysis(FileValueMeta meta, ImageAiAnalysis step, ImagingMeta? result) {
        if (meta is not ImageFileMeta image) return;
        image.AiAnalysis = step;
        if (result == null) return;
        if (!string.IsNullOrWhiteSpace(result.Title)) image.Title = result.Title;
        if (!string.IsNullOrWhiteSpace(result.Description)) image.Description = result.Description;
        if (result.Keywords.Length > 0) image.Keywords = result.Keywords;
        image.FocusPoint = result.Focus == null ? null : new ImageFocusPoint(result.Focus.X, result.Focus.Y);
        image.Objects = result.Objects.Length == 0 ? null : [.. result.Objects.Select(o => new ImageObjectInfo {
            Type = o.ObjectType, Name = o.Name, Confidence = o.Confidence, X = o.X, Y = o.Y, Width = o.Width, Height = o.Height,
        })];
    }
    static void applyText(FileValue value, FileValueMeta meta, FileToTextResult text) {
        value.TextExtract = text.Text;
        if (string.IsNullOrWhiteSpace(meta.Title) && !string.IsNullOrWhiteSpace(text.Title)) meta.Title = text.Title;
        if (string.IsNullOrWhiteSpace(meta.Author) && !string.IsNullOrWhiteSpace(text.Author)) meta.Author = text.Author;
        if (!string.IsNullOrWhiteSpace(text.Language)) meta.Language = text.Language;
        if (meta is DocumentFileMeta document) document.Pages = text.Pages;
        if (meta is MediaFileMeta media && text.Duration is double seconds) media.Duration = TimeSpan.FromSeconds(seconds);
    }

    async Task<(ImageAiAnalysis Step, ImagingMeta? Result)> analyseImageAsync(FileValue value, CancellationToken cancellationToken) {
        var imaging = _analysisImaging ?? throw new Exception("No imaging provider. ");
        var operations = await imagingOperationsAsync(imaging, cancellationToken);
        ImageAiAnalysis skipped(string reason) => new() { CompletedUtc = DateTime.UtcNow, Provider = imaging.Name, Error = reason };
        if (!operations.IsAvailable(imageToMetaOperation)) throw new Exception("The imaging service does not offer image analysis (" + imageToMetaOperation + ") at the moment. ");
        var image = await imageForAnalysisAsync(value, operations.MaxFileBytes);
        if (image == null) return (skipped("The image could not be made into a JPEG, PNG or WebP within " + operations.MaxFileBytes.ToByteString() + ". "), null);
        try {
            var result = await imaging.ImageToMetaAsync(image, analysisLanguage(), false, cancellationToken);
            return (new ImageAiAnalysis { CompletedUtc = DateTime.UtcNow, Provider = imaging.Name, Language = result.Language }, result);
        } catch (RelatudeServiceException error) when (error.StatusCode is 413 or 415) {
            return (skipped(error.Reason ?? error.Message), null);
        }
    }
    async Task<byte[]?> imageForAnalysisAsync(FileValue value, long maxBytes) {
        var limit = maxBytes > 0 ? maxBytes : long.MaxValue;
        if (value.Format is FileFormat.Jpeg or FileFormat.Png or FileFormat.Webp && value.Size <= limit) {
            await using var original = await GetFileStream(value);
            return await readAllAsync(original);
        }
        if (value.PropertyPath == null || !_fileConversionEngine.CanConvert(value.Format, FileFormat.Jpeg)) return null;
        var conversion = await GetFileStreamAndState(value.PropertyPath, new FileAdjustmentImage { RequestedFormat = FileFormat.Jpeg, Quality = 90 }, 120_000, _defaultQueryCtx);
        await using var converted = conversion.Stream;
        if (!conversion.IsReady) {
            await _fileConversionEngine.CancelRunning(conversion.ConversionId, false);
            throw new Exception("The image took too long to convert to JPEG. ");
        }
        var bytes = await readAllAsync(converted);
        return bytes.Length <= limit ? bytes : null;
    }

    async Task<(FileTextExtraction Step, FileToTextResult? Result)> extractTextAsync(FileValue value, CancellationToken cancellationToken) {
        var reader = _analysisFileToText ?? throw new Exception("No file-to-text provider. ");
        FileTextExtraction skipped(string reason) => new() { CompletedUtc = DateTime.UtcNow, Provider = reader.Name, Error = reason };
        var formats = await fileToTextFormatsAsync(reader, cancellationToken);
        var format = formats.ForFileName(value.Name);
        var recording = value.FileType is FileType.Audio or FileType.Video || format?.Group is "audio" or "video";
        if (!recording) {
            if (format == null) return (skipped("The file-to-text service does not know files named like \"" + value.Name + "\". "), null);
            if (!format.Available) return (skipped("The file-to-text service does not read " + format.Name + " files. "), null);
            if (formats.MaxFileBytes > 0 && value.Size > formats.MaxFileBytes) return (skipped("The file is larger than the " + formats.MaxFileBytes.ToByteString() + " the file-to-text service reads. "), null);
        }
        try {
            await using var stream = await GetFileStream(value);
            var languages = analysisLanguage() is { } language ? new[] { language } : null;
            var result = await reader.ExtractTextAsync(stream, value.Name, languages, false, cancellationToken);
            return (new FileTextExtraction {
                CompletedUtc = DateTime.UtcNow,
                Provider = reader.Name,
                Format = result.Format,
                Characters = result.Characters,
                Truncated = result.Truncated,
                Ocr = result.Ocr,
                Timed = result.Timed,
            }, result);
        } catch (RelatudeServiceException error) when (error.StatusCode is 413 or 415) {
            return (skipped(error.Reason ?? error.Message), null);
        }
    }

    async Task<ImagingOperations> imagingOperationsAsync(IImagingProvider imaging, CancellationToken cancellationToken) {
        if (_imagingOperations is { } known && DateTime.UtcNow - known.ReadUtc < _serviceInfoMaxAge) return known.Operations;
        var operations = await imaging.GetOperationsAsync(cancellationToken);
        _imagingOperations = (DateTime.UtcNow, operations);
        return operations;
    }
    async Task<FileToTextFormats> fileToTextFormatsAsync(IFileToTextProvider reader, CancellationToken cancellationToken) {
        if (_fileToTextFormats is { } known && DateTime.UtcNow - known.ReadUtc < _serviceInfoMaxAge) return known.Formats;
        var formats = await reader.GetFormatsAsync(cancellationToken);
        _fileToTextFormats = (DateTime.UtcNow, formats);
        return formats;
    }
    string? analysisLanguage() {
        var culture = _settings.DefaultCultureCode;
        if (string.IsNullOrWhiteSpace(culture)) return null;
        var dash = culture.IndexOfAny(['-', '_']);
        return (dash > 0 ? culture[..dash] : culture).ToLowerInvariant();
    }
    static async Task<byte[]> readAllAsync(Stream stream) {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
