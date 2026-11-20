using System.Collections.Concurrent;
using Relatude.DB.Common;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.FileConversion;
using Relatude.DB.FileToText;
using Relatude.DB.Imaging;
using Relatude.DB.Datamodels;
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
        if (fileType == FileType.Image && (property.ImageMetaAnalysis ?? _settings.EnableImageMetaAnalysisByDefault)) kinds |= FileAnalysisKinds.ImageMeta;
        if (fileType == FileType.Image && value.Format != FileFormat.Svg && (property.ImageCropModeAnalysis ?? _settings.EnableImageCropModeAnalysisByDefault)) kinds |= FileAnalysisKinds.ImageCropMode;
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
        if (meta is ImageFileMeta { AiAnalysis: not null }) kinds |= FileAnalysisKinds.ImageMeta;
        if (meta is ImageFileMeta { CropModeAnalysis: not null }) kinds |= FileAnalysisKinds.ImageCropMode;
        if (meta.TextExtraction != null) kinds |= FileAnalysisKinds.Text;
        return kinds;
    }
    /// <summary>What can be learnt of the file here at all: the picture's with an imaging provider (not an SVG's crop mode), the text with a file-to-text one.</summary>
    FileAnalysisKinds possibleFileAnalyses(FileValue value) {
        if (value.IsEmpty) return FileAnalysisKinds.None;
        var kinds = FileAnalysisKinds.None;
        if (_analysisImaging != null && value.FileType == FileType.Image) {
            kinds |= FileAnalysisKinds.ImageMeta;
            if (value.Format != FileFormat.Svg) kinds |= FileAnalysisKinds.ImageCropMode;
        }
        if (_analysisFileToText != null) kinds |= FileAnalysisKinds.Text;
        return kinds;
    }
    FileAnalysisKinds missingFileAnalyses(FilePropertyModel property, FileValue value) =>
        GetWantedFileAnalyses(property, value) & ~GetCompletedFileAnalyses(value) & possibleFileAnalyses(value);

    public FileAnalysisState GetFileAnalyses(PropertyPath propertyPath, FileValue value) {
        if (value.IsEmpty || !Datamodel.Properties.TryGetValue(propertyPath.PropertyId, out var property) || property is not FilePropertyModel fileProperty) return new(default, default, default);
        var possible = possibleFileAnalyses(value);
        if (possible == FileAnalysisKinds.None) return new(default, default, default); // read for every file a list shows: no meta parsed for nothing
        var done = GetCompletedFileAnalyses(value);
        return new(GetWantedFileAnalyses(fileProperty, value) & possible, done, possible) { Queued = queuedAnalyses(value) & ~done };
    }

    // the analyses waiting in the queue or under way there, by file and property, which a file's state tells as queued so
    // an editor can follow them; not kept across a restart, though the queue's tasks are
    readonly ConcurrentDictionary<(Guid FileId, Guid PropertyId), FileAnalysisKinds> _analysesQueued = new();
    FileAnalysisKinds queuedAnalyses(FileValue value) =>
        value.PropertyPath != null && _analysesQueued.TryGetValue((value.FileId, value.PropertyPath.PropertyId), out var kinds) ? kinds : FileAnalysisKinds.None;

    bool anyFileAnalysisPossible() {
        if (_analysisImaging == null && _analysisFileToText == null) return false;
        if (_settings.EnableImageMetaAnalysisByDefault || _settings.EnableImageCropModeAnalysisByDefault || _settings.EnableFileTextIndexingByDefault || _settings.EnableVideoAndAudioFileTextIndexingByDefault) return true;
        return Datamodel.Properties.Values.OfType<FilePropertyModel>().Any(p => p.ImageMetaAnalysis == true || p.ImageCropModeAnalysis == true || p.FileTextIndexing == true || p.VideoAndAudioFileTextIndexing == true);
    }

    /// <summary>The analyses a file wants and lacks, queued to run in the background, in the revision it is in (null: a
    /// node without revisions).</summary>
    bool queueFileAnalysisIfMissing(FileValue value, Guid? revisionId) {
        if (value.IsEmpty || value.PropertyPath == null) return false;
        if (!Datamodel.Properties.TryGetValue(value.PropertyPath.PropertyId, out var property) || property is not FilePropertyModel fileProperty) return false;
        var missing = missingFileAnalyses(fileProperty, value);
        if (missing == FileAnalysisKinds.None) return false;
        _analysesQueued.AddOrUpdate((value.FileId, value.PropertyPath.PropertyId), missing, (_, kinds) => kinds | missing);
        EnqueueTask(new FileAnalysisTask(value.PropertyPath, value.FileId, missing, revisionId));
        return true;
    }

    /// <summary>The id of the revision a context reads of the node a property is in: the one a write in it went to. Null for a
    /// node without revisions, or one the context reads nothing of.</summary>
    Guid? revisionIdOf(PropertyPath path, QueryContext ctx) {
        try {
            var key = path.NodePath.NodeKey;
            if (!TryGet(key.HasGuid ? key.Guid : GetGuid(key.Int), out var node, ctx)) return null;
            if (node is NodeDataRevision revision) return revision.RevisionId == Guid.Empty ? null : revision.RevisionId;
            return GetRevisions(node.Id).FirstOrDefault(r => r.RevisionKey == node.RevisionKey && r.RevisionId != Guid.Empty)?.RevisionId;
        } catch {
            return null;
        }
    }

    /// <summary>The context a queued analysis reads and writes the file with: the store's own, selecting the revision the
    /// file went into when the task names one that is still there, whatever its key now (a draft published meanwhile).</summary>
    QueryContext analysisContextOf(FileAnalysisTask task) {
        if (task.RevisionId is not Guid revisionId) return _defaultQueryCtx;
        try {
            var key = task.PropertyPath.NodePath.NodeKey;
            var nodeId = key.HasGuid ? key.Guid : GetGuid(key.Int);
            var revision = GetRevisions(nodeId).FirstOrDefault(r => r.RevisionId == revisionId);
            if (revision == null) return _defaultQueryCtx; // gone: the file is looked for where readers find it
            var ctx = _defaultQueryCtx.WithCancellation(_defaultQueryCtx.CancellationToken); // a copy of its own
            ctx.SelectedRevisions = [new NodeIdAndRevisionId(nodeId, revision.RevisionKey)];
            return ctx;
        } catch {
            return _defaultQueryCtx;
        }
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
        var fileValues = new List<(FileValue Value, Guid? Revision)>();
        const int batchSize = 1000;
        for (var offset = 0; offset < ids.Length; offset += batchSize) {
            fileValues.Clear();
            var end = Math.Min(offset + batchSize, ids.Length);
            _lock.EnterReadLock();
            try {
                for (var i = offset; i < end; i++) {
                    if (_nodes.TryGet(ids[i], out var node, out _)) collectFileValuesByRevision(node, fileValues);
                }
            } finally {
                _lock.ExitReadLock();
            }
            // revisions made of another share its files: each analysed once, in a published revision where one holds it
            foreach (var (fileValue, revision) in fileValues.DistinctBy(v => (v.Value.PropertyPath, v.Value.FileId))) {
                if (queueFileAnalysisIfMissing(fileValue, revision)) queued++;
            }
        }
        if (queued > 0) LogInfo("Queued " + queued.To1000N() + " file" + (queued == 1 ? "" : "s") + " for analysis. ");
        return queued;
    }

    internal async Task<FileAnalysisOutcome> RunFileAnalysisTaskAsync(FileAnalysisTask task, CancellationToken cancellationToken) {
        validateDatabaseState();
        try {
            var ctx = analysisContextOf(task);
            if (!TryGetValue<FileValue>(task.PropertyPath, out var value, ctx) || value.IsEmpty || value.FileId != task.FileId) return new(true, "The file was replaced or removed before it was analysed. ");
            if (!Datamodel.Properties.TryGetValue(task.PropertyPath.PropertyId, out var property) || property is not FilePropertyModel fileProperty) return new(true, "The property is no longer a file property. ");
            var kinds = task.Kinds & missingFileAnalyses(fileProperty, value);
            if (kinds == FileAnalysisKinds.None) return new(true, "Nothing left to analyse. ");
            var steps = await analyseAsync(task.PropertyPath, value, kinds, false, ctx, cancellationToken, system: true);
            return new(!steps.Any(s => s.State == FileAnalysisStepState.Failed), string.Join("; ", steps.Select(s => s.Summary.TrimEnd(' ', '.'))) + ". ");
        } finally {
            _analysesQueued.TryRemove((task.FileId, task.PropertyPath.PropertyId), out _);
        }
    }

    public Task<FileAnalysisReport> AnalyseFileAsync(PropertyPath propertyPath, FileAnalysisKinds kinds, bool force = false, QueryContext? ctx = null, CancellationToken cancellationToken = default)
        => AnalyseFileAsync(propertyPath, kinds, force, ctx, null, cancellationToken);

    public async Task<FileAnalysisReport> AnalyseFileAsync(PropertyPath propertyPath, FileAnalysisKinds kinds, bool force, QueryContext? ctx, IProgress<FileAnalysisProgress>? progress, CancellationToken cancellationToken = default) {
        validateDatabaseState();
        ctx ??= _defaultQueryCtx;
        if (!Datamodel.Properties.TryGetValue(propertyPath.PropertyId, out var property) || property is not FilePropertyModel) throw new Exception("Property is not a file");
        if (!TryGetValue<FileValue>(propertyPath, out var value, ctx) || value.IsEmpty) throw new Exception("The property holds no file. ");
        ensureMayWriteFile(propertyPath, ctx);
        var steps = await analyseAsync(propertyPath, value, kinds, force, ctx, cancellationToken, progress: progress);
        return new(TryGetValue<FileValue>(propertyPath, out var now, ctx) && !now.IsEmpty && now.FileId == value.FileId ? now : null, steps);
    }

    static readonly FileAnalysisKinds[] _analysisOrder = [FileAnalysisKinds.ImageMeta, FileAnalysisKinds.ImageCropMode, FileAnalysisKinds.Text];

    /// <summary>
    /// The analyses asked for, at once, each kept with the file as it is done, in the version the context reads. Without
    /// force only those not done before; with it, again, the results replacing what was set by hand (a title, a crop
    /// mode), which a run without it leaves.
    /// </summary>
    // system: the queue's analysis of a file written before, not a user's asking, so not checked for write access
    // progress: told how far the text extraction has come, and that each of the others started
    async Task<FileAnalysisStepReport[]> analyseAsync(PropertyPath path, FileValue value, FileAnalysisKinds kinds, bool force, QueryContext ctx, CancellationToken cancellationToken, bool system = false,
        IProgress<FileAnalysisProgress>? progress = null) {
        var possible = possibleFileAnalyses(value);
        var done = GetCompletedFileAnalyses(value);
        // one picture for both of the picture's analyses
        Task<byte[]?>? picture = null;
        Task<byte[]?> pictureOnce(long maxBytes) => picture ??= imageForAnalysisAsync(value, maxBytes);
        var steps = new List<Task<FileAnalysisStepReport>>();
        foreach (var kind in _analysisOrder) {
            if (!kinds.HasFlag(kind)) continue;
            if (!possible.HasFlag(kind)) steps.Add(Task.FromResult(new FileAnalysisStepReport(kind, FileAnalysisStepState.NotPossible, nameOf(kind) + ": not for this file here")));
            else if (!force && done.HasFlag(kind)) steps.Add(Task.FromResult(new FileAnalysisStepReport(kind, FileAnalysisStepState.DoneBefore, nameOf(kind) + ": done before")));
            else steps.Add(analyseOnceAsync(path + "|" + value.FileId + "|" + kind, kind, cancellationToken, () => {
                progress?.Report(new FileAnalysisProgress(kind, char.ToUpperInvariant(nameOf(kind)[0]) + nameOf(kind)[1..], 0));
                return kind switch {
                    FileAnalysisKinds.ImageMeta => imageMetaStepAsync(path, value, pictureOnce, force, ctx, system, cancellationToken),
                    FileAnalysisKinds.ImageCropMode => cropModeStepAsync(path, value, pictureOnce, force, ctx, system, cancellationToken),
                    _ => textStepAsync(path, value, ctx, system, cancellationToken, progress, force),
                };
            }));
        }
        return await Task.WhenAll(steps);
    }

    static string nameOf(FileAnalysisKinds kind) => kind switch {
        FileAnalysisKinds.ImageMeta => "image meta analysis",
        FileAnalysisKinds.ImageCropMode => "crop mode analysis",
        FileAnalysisKinds.Text => "text extraction",
        _ => kind.ToString(),
    };

    // the analyses under way, by file and kind: one asked for again meanwhile (by the queue, or another user) waits for
    // it, rather than paying for it twice
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<FileAnalysisStepReport>> _analysesRunning = new();

    async Task<FileAnalysisStepReport> analyseOnceAsync(string key, FileAnalysisKinds kind, CancellationToken cancellationToken, Func<Task<FileAnalysisStepReport>> run) {
        while (true) {
            var mine = new TaskCompletionSource<FileAnalysisStepReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = _analysesRunning.GetOrAdd(key, mine.Task);
            if (running != mine.Task) {
                try {
                    return await running.WaitAsync(cancellationToken);
                } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                    continue; // the run waited for was stopped: this one goes on
                }
            }
            try {
                var report = await run();
                mine.SetResult(report);
                return report;
            } catch (OperationCanceledException) {
                mine.SetCanceled();
                throw;
            } catch (Exception error) {
                var report = failed(kind, error);
                mine.SetResult(report);
                return report;
            } finally {
                _analysesRunning.TryRemove(new KeyValuePair<string, Task<FileAnalysisStepReport>>(key, mine.Task));
            }
        }
    }

    /// <summary>A step that failed, said: tried again at the next text reindex, as nothing is kept.</summary>
    static FileAnalysisStepReport failed(FileAnalysisKinds kind, Exception error) => new(kind, FileAnalysisStepState.Failed, nameOf(kind) + " failed: " + error.Message);
    static FileAnalysisStepReport replaced(FileAnalysisKinds kind) => new(kind, FileAnalysisStepState.Failed, nameOf(kind) + ": the file was replaced before the result could be kept");

    async Task<FileAnalysisStepReport> imageMetaStepAsync(PropertyPath path, FileValue value, Func<long, Task<byte[]?>> picture, bool force, QueryContext ctx, bool system, CancellationToken cancellationToken) {
        const FileAnalysisKinds kind = FileAnalysisKinds.ImageMeta;
        try {
            var (step, result) = await analyseImageAsync(value, picture, cancellationToken);
            if (!await storeFileAnalysisAsync(path, value.FileId, ctx, system, (_, meta) => applyImageAnalysis(meta, step, result, force))) return replaced(kind);
            return step.Succeeded
                ? new(kind, FileAnalysisStepState.Done, "image meta analysis: " + (result?.Keywords.Length ?? 0) + " keywords" + (result?.Focus != null ? ", a focus point" : ""), result?.Credits)
                : new(kind, FileAnalysisStepState.Skipped, "image meta analysis skipped: " + step.Error);
        } catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) {
            return failed(kind, error);
        }
    }

    async Task<FileAnalysisStepReport> cropModeStepAsync(PropertyPath path, FileValue value, Func<long, Task<byte[]?>> picture, bool force, QueryContext ctx, bool system, CancellationToken cancellationToken) {
        const FileAnalysisKinds kind = FileAnalysisKinds.ImageCropMode;
        try {
            var (step, mode, image, credits) = await analyseCropModeAsync(value, picture, cancellationToken);
            // the background, from the picture's edges, where the crop mode it is left with may show room around it
            var background = step.Succeeded && mode != ImageCropMode.Fill && image != null ? await edgeColourAsync(value, image) : null;
            ImageCropMode? kept = null;
            var backgroundSet = false;
            var stored = await storeFileAnalysisAsync(path, value.FileId, ctx, system, (_, meta) => {
                if (meta is not ImageFileMeta pictureMeta) return;
                pictureMeta.CropModeAnalysis = step;
                if (mode != null && (force || pictureMeta.CropMode == null)) pictureMeta.CropMode = mode;
                if (background != null && showsBackground(pictureMeta.CropMode) && (force || pictureMeta.BackgroundColor == null)) {
                    pictureMeta.BackgroundColor = background;
                    backgroundSet = true;
                }
                kept = pictureMeta.CropMode;
            });
            if (!stored) return replaced(kind);
            if (!step.Succeeded) return new(kind, FileAnalysisStepState.Skipped, "crop mode analysis skipped: " + step.Error);
            var said = step.Graphic is bool graphic ? (graphic ? "a graphic" : "a photograph") + (step.Certainty is int sure ? " (" + sure + "% sure)" : "") : "neither a graphic nor a photograph";
            var summary = "crop mode analysis: " + said + ", " + (mode != null && kept == mode ? "set to " + mode : "crop mode " + (kept?.ToString() ?? "unset") + " kept")
                + (backgroundSet ? ", background " + background + " from its edges" : "");
            return new(kind, FileAnalysisStepState.Done, summary, credits);
        } catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) {
            return failed(kind, error);
        }
    }

    async Task<FileAnalysisStepReport> textStepAsync(PropertyPath path, FileValue value, QueryContext ctx, bool system, CancellationToken cancellationToken,
        IProgress<FileAnalysisProgress>? progress = null, bool force = false) {
        const FileAnalysisKinds kind = FileAnalysisKinds.Text;
        try {
            var (step, text) = await extractTextAsync(path, value, ctx, cancellationToken, progress == null ? null
                : new TextProgress(p => progress.Report(new FileAnalysisProgress(kind, p.Message, p.Share) { UpTo = p.UpTo, Seconds = p.Seconds })), force);
            progress?.Report(new FileAnalysisProgress(kind, "Keeping the text", 1));
            var stored = await storeFileAnalysisAsync(path, value.FileId, ctx, system, (file, meta) => {
                meta.TextExtraction = step;
                if (text != null) applyText(file, meta, text);
            });
            if (!stored) return replaced(kind);
            return step.Succeeded
                ? new(kind, FileAnalysisStepState.Done, "text: " + step.Characters.To1000N() + " characters", text?.Credits)
                : new(kind, FileAnalysisStepState.Skipped, "text extraction skipped: " + step.Error);
        } catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) {
            return failed(kind, error);
        }
    }

    async Task<bool> storeFileAnalysisAsync(PropertyPath path, Guid fileId, QueryContext ctx, bool system, Action<FileValue, FileValueMeta> apply) {
        var nodeId = _guids.ValidateAndReturnIntId(path.NodePath.NodeKey);
        var lockId = await RequestLockAsync(nodeId, 10_000, 30_000);
        try {
            if (!TryGetValue<FileValue>(path, out var current, ctx) || current.IsEmpty || current.FileId != fileId) return false;
            var updated = current.Copy();
            var meta = updated.GetMeta().AsKindFor(updated.FileType);
            apply(updated, meta);
            updated.SetMeta(meta);
            var t = new TransactionData { LockExcemptions = [lockId], SystemWrite = system };
            t.ForceUpdateProperty(path, updated);
            Execute(t, false, false, ctx);
            return true;
        } finally {
            ReleaseLock(lockId);
        }
    }
    /// <summary>What the meta analysis found, kept: without force, a title, description, keywords and focus point set before (by a person) stay.</summary>
    static void applyImageAnalysis(FileValueMeta meta, ImageAiAnalysis step, ImagingMeta? result, bool force) {
        if (meta is not ImageFileMeta image) return;
        image.AiAnalysis = step;
        if (result == null) return;
        if (!string.IsNullOrWhiteSpace(result.Title) && (force || string.IsNullOrWhiteSpace(image.Title))) image.Title = result.Title;
        if (!string.IsNullOrWhiteSpace(result.Description) && (force || string.IsNullOrWhiteSpace(image.Description))) image.Description = result.Description;
        if (result.Keywords.Length > 0 && (force || image.Keywords is not { Length: > 0 })) image.Keywords = result.Keywords;
        if (result.Focus != null && (force || image.FocusPoint == null)) image.FocusPoint = new ImageFocusPoint(result.Focus.X, result.Focus.Y);
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

    async Task<(ImageAiAnalysis Step, ImagingMeta? Result)> analyseImageAsync(FileValue value, Func<long, Task<byte[]?>> picture, CancellationToken cancellationToken) {
        var imaging = _analysisImaging ?? throw new Exception("No imaging provider. ");
        var operations = await imagingOperationsAsync(imaging, cancellationToken);
        ImageAiAnalysis skipped(string reason) => new() { CompletedUtc = DateTime.UtcNow, Provider = imaging.Name, Error = reason };
        if (!operations.IsAvailable(imageToMetaOperation)) throw new Exception("The imaging service does not offer image analysis (" + imageToMetaOperation + ") at the moment. ");
        var image = await picture(operations.MaxFileBytes);
        if (image == null) return (skipped("The image could not be made into a JPEG, PNG or WebP within " + operations.MaxFileBytes.ToByteString() + ". "), null);
        try {
            var result = await imaging.ImageToMetaAsync(image, analysisLanguage(), false, cancellationToken);
            return (new ImageAiAnalysis { CompletedUtc = DateTime.UtcNow, Provider = imaging.Name, Language = result.Language }, result);
        } catch (RelatudeServiceException error) when (error.StatusCode is 413 or 415) {
            return (skipped(error.Reason ?? error.Message), null);
        }
    }

    /// <summary>
    /// Asked of a picture, yes or no: a graphic is fitted whole, as a crop cuts into it; a photograph is cropped about its
    /// focus point to fill a shape. The CMS interface asks the same (GRAPHIC_QUESTION in imaging/auto.ts).
    /// </summary>
    public const string GraphicQuestion =
        "Is this picture a graphic rather than a photograph: a logo, an icon, an illustration or drawing, a diagram, a chart, a map, "
        + "a screenshot, a document or a page of text, or a product photographed alone on a plain white or see-through background? "
        + "It is not a graphic when it is a photograph of a scene, a landscape, a place, a room, food, an animal or people, "
        + "portraits included, or of a product in a setting.";
    /// <summary>How sure Image AI must be (0 to 100) before the crop mode is set: less, and the picture keeps the one it has.</summary>
    public const int CropModeCertainty = 70;
    const string askBoolOperation = "ask-about-image-bool";
    const string askOperation = "ask-about-image";

    async Task<(ImageCropModeAnalysis Step, ImageCropMode? Mode, byte[]? Image, int? Credits)> analyseCropModeAsync(FileValue value, Func<long, Task<byte[]?>> picture, CancellationToken cancellationToken) {
        var imaging = _analysisImaging ?? throw new Exception("No imaging provider. ");
        var operations = await imagingOperationsAsync(imaging, cancellationToken);
        ImageCropModeAnalysis skipped(string reason) => new() { CompletedUtc = DateTime.UtcNow, Provider = imaging.Name, Error = reason };
        var asksBool = operations.IsAvailable(askBoolOperation);
        if (!asksBool && !operations.IsAvailable(askOperation)) throw new Exception("The imaging service does not answer questions about pictures (" + askBoolOperation + ") at the moment. ");
        var image = await picture(operations.MaxFileBytes);
        if (image == null) return (skipped("The image could not be made into a JPEG, PNG or WebP within " + operations.MaxFileBytes.ToByteString() + ". "), null, null, null);
        try {
            if (asksBool) {
                var answer = await imaging.AskAboutImageBoolAsync(image, GraphicQuestion, false, cancellationToken);
                var step = new ImageCropModeAnalysis { CompletedUtc = DateTime.UtcNow, Provider = imaging.Name, Graphic = answer.Answer, Certainty = answer.Certainty };
                ImageCropMode? mode = answer.Certainty >= CropModeCertainty ? (answer.Answer ? ImageCropMode.Fit : ImageCropMode.Fill) : null;
                return (step, mode, image, answer.Credits);
            } else {
                // a service from before the yes-or-no question, asked in words
                var answer = await imaging.AskAboutImageAsync(image, GraphicQuestion + " Answer with one word: yes or no.", false, cancellationToken);
                var word = new string([.. answer.Answer.Trim().ToLowerInvariant().TakeWhile(char.IsLetter)]);
                bool? graphic = word == "yes" ? true : word == "no" ? false : null;
                var step = new ImageCropModeAnalysis { CompletedUtc = DateTime.UtcNow, Provider = imaging.Name, Graphic = graphic };
                return (step, graphic is bool g ? (g ? ImageCropMode.Fit : ImageCropMode.Fill) : null, image, answer.Credits);
            }
        } catch (RelatudeServiceException error) when (error.StatusCode is 413 or 415) {
            return (skipped(error.Reason ?? error.Message), null, null, null);
        }
    }

    /// <summary>Whether a crop mode may show room around the picture, which its background fills: Fit (and none, which is Fit) and Auto.</summary>
    static bool showsBackground(ImageCropMode? mode) => mode is null or ImageCropMode.Fit or ImageCropMode.Auto;

    /// <summary>
    /// The colour of the picture's edges, read from the file itself where it was sent to the AI as a JPEG that lost what
    /// is see-through in it (a GIF or BMP), else from the picture sent. None when it cannot be read, or its edges are see-through.
    /// </summary>
    async Task<string?> edgeColourAsync(FileValue value, byte[] sent) {
        try {
            var data = sent;
            if (value.Format is FileFormat.Gif or FileFormat.Bmp && value.Size <= 64 * 1024 * 1024) {
                await using var original = await GetFileStream(value);
                data = await readAllAsync(original);
            }
            return ImageEdges.ColorOf(data);
        } catch (Exception error) {
            LogWarning("The edges of \"" + value.Name + "\" could not be read for its background: " + error.Message);
            return null;
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

    /// <summary>The text's progress handed on at once, on the thread it is told on.</summary>
    sealed class TextProgress(Action<FileToTextProgress> report) : IProgress<FileToTextProgress> {
        public void Report(FileToTextProgress value) => report(value);
    }

    /// <summary>The progress of a transcript's conversion told as the text's.</summary>
    sealed class ConversionAsText(IProgress<FileToTextProgress> text) : IProgress<FileConversionProgressInfo> {
        public void Report(FileConversionProgressInfo value) => text.Report(new FileToTextProgress(value.Message ?? "Transcribing", Math.Clamp(value.ProgressPercentage, 0, 100) / 100.0));
    }

    /// <summary>
    /// The text of a file by the file-to-text provider. A stored recording or video is its transcript, a conversion
    /// (<see cref="FileAdjustmentTranscript"/>, made by <see cref="TranscriptConverter"/>): cached like any, so read from
    /// there when a site or code asked for it before, and made anew, its speech still kept, with <paramref name="force"/>.
    /// Any other file is read from its stream.
    /// </summary>
    async Task<(FileTextExtraction Step, FileToTextResult? Result)> extractTextAsync(PropertyPath path, FileValue value, QueryContext ctx, CancellationToken cancellationToken,
        IProgress<FileToTextProgress>? progress = null, bool force = false) {
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
            var languages = analysisLanguage() is { } language ? new[] { language } : null;
            FileToTextResult result;
            if (value.FileType is FileType.Audio or FileType.Video && _fileConversionEngine.CanConvert(value.Format, FileFormat.Json)) {
                var transcript = (FileAdjustmentTranscript)resolveAdaptiveFormat(value, new FileAdjustmentTranscript(), out _);
                var (info, source) = conversionOf(path, value, transcript);
                if (force) clearTranscripts(info, transcript);
                var made = await _fileConversionEngine.ConvertAsync(info, transcript, source, progress == null ? null : new ConversionAsText(progress), cancellationToken);
                result = TranscriptData.FromBytes(made.Bytes).ToResult(value.Name, made.Kept);
            } else {
                await using var stream = await GetFileStream(value);
                result = progress == null ? await reader.ExtractTextAsync(stream, value.Name, languages, false, cancellationToken)
                    : await reader.ExtractTextAsync(stream, value.Name, languages, false, progress, cancellationToken);
            }
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
