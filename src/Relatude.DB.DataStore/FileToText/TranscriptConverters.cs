using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Relatude.DB.Common;
using Relatude.DB.FileConversion;

namespace Relatude.DB.FileToText;

/// <summary>
/// A transcript as the conversions keep it (<see cref="FileAdjustmentTranscript"/> with <see cref="FileFormat.Json"/>), which
/// every other format of it is written from: the text, a line for each stretch of speech beginning with when it is spoken
/// (<see cref="Transcript"/>), and what was learnt of it. Never the credits left on the license, as a site may serve it.
/// </summary>
public sealed record TranscriptData {
    /// <summary>The transcript as the database keeps it: for a recording, timed lines; text without times when the provider gave none.</summary>
    public string Text { get; init; } = string.Empty;
    /// <summary>Whether <see cref="Text"/> is timed lines.</summary>
    public bool Timed { get; init; }
    /// <summary>The language spoken, as the service heard it.</summary>
    public string? Language { get; init; }
    /// <summary>How long the recording plays, in seconds.</summary>
    public double? Duration { get; init; }
    public int Characters { get; init; }
    /// <summary>The text is not all that was said: cut at a limit.</summary>
    public bool Truncated { get; init; }
    /// <summary>The kind of file the service took it for.</summary>
    public string? Format { get; init; }
    /// <summary>What made it.</summary>
    public string? Provider { get; init; }
    /// <summary>What making it cost.</summary>
    public int Credits { get; init; }
    public DateTime MadeUtc { get; init; }

    static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static TranscriptData Of(FileToTextResult result, string? provider) => new() {
        Text = result.Text,
        Timed = result.Timed,
        Language = result.Language,
        Duration = result.Duration,
        Characters = result.Characters,
        Truncated = result.Truncated,
        Format = result.Format,
        Provider = provider,
        Credits = result.Credits,
        MadeUtc = DateTime.UtcNow,
    };
    /// <summary>As the file-to-text provider gives it, for code that reads a provider's results. <c>Cached</c>, when it was kept from before.</summary>
    public FileToTextResult ToResult(string? fileName, bool cached) =>
        new(Text, Format ?? "audio", fileName, Characters, null, Truncated, false, null, null, Language, cached ? 0 : Credits, 0, cached) { Timed = Timed, Duration = Duration };
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, _json);
    public static TranscriptData FromBytes(byte[] bytes) =>
        JsonSerializer.Deserialize<TranscriptData>(bytes, _json) ?? throw new InvalidDataException("The transcript kept is not one. ");
    /// <summary>The transcript in a format a transcript is given in (<see cref="FileAdjustmentTranscript.IsTranscriptFormat"/>).</summary>
    public string Write(FileFormat format, bool timestamps = true) => format switch {
        FileFormat.Txt => timestamps || !Timed ? Text : Transcript.RemoveTimestamps(Text),
        FileFormat.Vtt => Transcript.ToWebVtt(Timed ? Transcript.Parse(Text) : []),
        FileFormat.Srt => Transcript.ToSrt(Timed ? Transcript.Parse(Text) : []),
        FileFormat.Json => Encoding.UTF8.GetString(ToBytes()),
        _ => throw new NotSupportedException("A transcript is given as JSON, TXT, VTT or SRT, not " + format.ToString().ToUpper() + ". "),
    };
}

/// <summary>
/// A transcript's converters write their status, while it is not made, as a transcript of that format would read: a
/// WebVTT file with a note and no cues (a player shows nothing), a line of text, a small JSON, an empty SubRip file.
/// </summary>
public static class TranscriptStatus {
    public static byte[] Of(FileFormat format, List<string> text) => Of(format, string.Join(" ", text.Where(t => !string.IsNullOrWhiteSpace(t))));
    public static byte[] Of(FileFormat format, string said) {
        said = said.Trim();
        return Encoding.UTF8.GetBytes(format switch {
            FileFormat.Vtt => "WEBVTT\n\nNOTE " + said.Replace("-->", "->") + "\n",
            FileFormat.Json => JsonSerializer.Serialize(new { status = said }),
            FileFormat.Srt => string.Empty,
            _ => said + "\n",
        });
    }
}

/// <summary>
/// Makes the transcript of a recording or of the sound of a video (<see cref="FileAdjustmentTranscript"/> as
/// <see cref="FileFormat.Json"/>) with the database's file-to-text provider. A provider that
/// <see cref="IFileToTextProvider.TranscribesRecordings"/> is handed the recording with its speech made by the cached
/// conversions (<see cref="SpeechSound.Adjustment"/>, part by part as it asks), so the sound is taken out once and kept;
/// any other gets the file itself. The service's wait, which says nothing of how far it has come, is crept through in
/// the live status, slower the longer the sound. The provider is asked for when a transcript is made, as a store is
/// given it after its converters.
/// </summary>
public sealed class TranscriptConverter(Func<IFileToTextProvider?> provider) : IFileConverter {
    FileConversionEngine? _engine;
    readonly ConcurrentDictionary<Guid, Working> _working = new();
    /// <summary>Two at once: each waits mostly for the service, and for its speech, which the video converter makes.</summary>
    public int ThreadCount { get; set; } = 2;
    public int CallDelayMs { get; set; } = 0;
    public void Initialize(FileConversionEngine conversionEngine) => _engine = conversionEngine;
    public bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed)
        => inBase is FileType.Audio or FileType.Video && outDetailed == FileFormat.Json;

    public Task<bool> CancelAsync(Guid key) {
        if (!_working.TryGetValue(key, out var working)) return Task.FromResult(false);
        try { working.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        return Task.FromResult(true);
    }

    public async Task<ConversionProgress> DoConvertWork(InputFileSource source, FileConversionInfo info) {
        var engine = _engine ?? throw new InvalidOperationException("The converter was not initialized. ");
        if (info.IdWithAdjustment.Adjustment is not FileAdjustmentTranscript adjustment) {
            return failed("A transcript is asked for with FileAdjustmentTranscript. ");
        }
        if (provider() is not { } reader) return failed("The database has no file-to-text service, which makes transcripts. ");
        var key = info.IdWithAdjustment.GetKey();
        using var working = new Working();
        _working[key] = working;
        try {
            var languages = adjustment.Language is { } language ? new[] { language } : null;
            FileToTextResult result;
            if (reader.TranscribesRecordings && engine.CanConvert(info.FromFormat, FileFormat.Ogg)) {
                result = await reader.TranscribeAsync(new ConvertedRecording(engine, info, source), languages, false, working, working.Cancellation.Token);
            } else {
                await using var file = await source.OpenInputStream();
                result = await reader.ExtractTextAsync(file, info.FileName, languages, false, working, working.Cancellation.Token);
            }
            var made = TranscriptData.Of(result, reader.Name);
            return new ConversionProgress(new FileConversionProgressInfo(FileConversionStatus.Ready, 100, 0), new MemoryStream(made.ToBytes(), writable: false));
        } catch (OperationCanceledException) when (working.Cancellation.IsCancellationRequested) {
            throw; // the engine keeps a cancelled conversion as no error
        } catch (RelatudeServiceException error) {
            return failed(error.Reason ?? error.Message);
        } catch (Exception error) {
            return failed(error.Message); // the reason kept with the failure, which a conversion that throws would lose
        } finally {
            _working.TryRemove(new KeyValuePair<Guid, Working>(key, working));
        }
    }
    static ConversionProgress failed(string why) => new(new FileConversionProgressInfo(FileConversionStatus.Error, 0, 0, why));

    public bool TryGetLiveStatus(Guid key, [MaybeNullWhen(false)] out FileConversionProgressInfo status) {
        status = _working.TryGetValue(key, out var working) ? working.Now() : null;
        return status != null;
    }
    public byte[] CreateStatusResponse(FileFormat requestedFormat, int width, int height, List<string> text, string textColor, string fillColor)
        => TranscriptStatus.Of(requestedFormat, text);

    /// <summary>A transcript being made: how to stop it, and what the provider last told of it, crept through while it waits for the service.</summary>
    sealed class Working : IProgress<FileToTextProgress>, IDisposable {
        public readonly CancellationTokenSource Cancellation = new();
        readonly object _lock = new();
        FileToTextProgress? _told;
        Stopwatch? _since;
        double _shown;

        public void Report(FileToTextProgress value) {
            lock (_lock) {
                _told = value;
                _since = Stopwatch.StartNew();
            }
        }

        /// <summary>
        /// As the provider told it, and while the service works on what was sent, which says nothing until it answers, a
        /// share creeping from where it stood toward where the answer puts it, slower the longer it takes (an hour of sound
        /// takes the service minutes), the time waited said. Never back: a part's start is told after the part before's answer.
        /// </summary>
        public FileConversionProgressInfo Now() {
            lock (_lock) {
                if (_told is not { } told) return new(FileConversionStatus.InProgress, 0, -1, "Starting");
                var share = told.Share;
                var message = told.Message;
                if (told.UpTo is { } upTo && upTo > told.Share && _since != null) {
                    var waited = _since.Elapsed.TotalSeconds;
                    // what the wait goes by: a few seconds, and more the longer the sound is
                    var expected = 8 + (told.Seconds ?? 60) * 0.12;
                    share = told.Share + (upTo - told.Share) * 0.95 * (1 - Math.Exp(-waited / expected));
                    var of = told.Seconds is { } seconds ? " " + LengthOf(seconds) + " of sound" : "";
                    message = told.Message + of + ", " + LengthOf(waited) + " so far";
                }
                _shown = Math.Max(_shown, Math.Clamp(share, 0, 1));
                return new(FileConversionStatus.InProgress, (int)Math.Round(_shown * 100), -1, message);
            }
        }

        public void Dispose() => Cancellation.Dispose();
    }

    /// <summary>A length for people: "25 s", "4 min 10 s", "1 h 5 min".</summary>
    public static string LengthOf(double seconds) {
        var s = (int)Math.Round(seconds);
        if (s < 60) return s.ToString(CultureInfo.InvariantCulture) + " s";
        var m = s / 60;
        if (m < 60) return m.ToString(CultureInfo.InvariantCulture) + " min" + (s % 60 > 0 ? " " + (s % 60).ToString(CultureInfo.InvariantCulture) + " s" : "");
        return (m / 60).ToString(CultureInfo.InvariantCulture) + " h " + (m % 60).ToString(CultureInfo.InvariantCulture) + " min";
    }

    /// <summary>
    /// The recording a transcript is made of, as the conversions have it: its length from the cached measuring
    /// (<see cref="FileAdjustmentMeta"/>) and its speech from the cached <see cref="SpeechSound"/> conversions of the same
    /// file, each made once and kept, first in line, told as it goes, cancelled with the transcript.
    /// </summary>
    sealed class ConvertedRecording(FileConversionEngine engine, FileConversionInfo info, InputFileSource source) : IRecording {
        public string? FileName => info.FileName;
        public FileFormat Format => info.FromFormat;
        // the bytes' own hash where the store has one: the same file uploaded again is the same recording
        public string? Identity => string.IsNullOrEmpty(info.Hash) ? "file " + info.IdWithAdjustment.FileId.ToString("N") : info.Hash.ToLowerInvariant();

        public async Task<TimeSpan?> LengthAsync(CancellationToken cancellationToken = default) {
            if (!engine.CanConvert(info.FromFormat, FileFormat.FileMetaJson)) return null;
            // the key the file's measuring uses (the file library's too), so usually made already
            var measured = await engine.ConvertAsync(info, new FileAdjustmentMeta(), source, null, cancellationToken);
            return BasicFileMeta.FromBytes(measured.Bytes)?.Duration is { } length && length > TimeSpan.Zero ? length : null;
        }

        public async Task<RecordingSpeech> SpeechAsync(TimeSpan? start, TimeSpan? duration, IProgress<FileConversionProgressInfo>? progress = null, CancellationToken cancellationToken = default) {
            var adjustment = SpeechSound.Adjustment(start is { } s ? (int)s.TotalMilliseconds : null, duration is { } d ? (int)d.TotalMilliseconds : null);
            var made = await engine.ConvertAsync(info, adjustment, source, progress, cancellationToken);
            return new RecordingSpeech(made.Bytes, made.Kept);
        }
    }
}

/// <summary>
/// Writes a transcript as text, subtitles or SubRip (<see cref="FileAdjustmentTranscript"/> as <see cref="FileFormat.Txt"/>,
/// <see cref="FileFormat.Vtt"/> or <see cref="FileFormat.Srt"/>) from its <see cref="FileFormat.Json"/> conversion, which it
/// asks the engine for and waits for: a converter of its own, so a format waiting for its transcript never holds the
/// slot the transcript needs. Calls no service and costs nothing once the transcript is made.
/// </summary>
public sealed class TranscriptFormatConverter : IFileConverter {
    FileConversionEngine? _engine;
    readonly ConcurrentDictionary<Guid, (CancellationTokenSource Cancellation, StrongBox<FileConversionProgressInfo?> Told)> _working = new();
    public int ThreadCount { get; set; } = 4;
    public int CallDelayMs { get; set; } = 0;
    public void Initialize(FileConversionEngine conversionEngine) => _engine = conversionEngine;
    public bool SupportsConversion(FileType inBase, FileFormat inDetailed, FileType outBase, FileFormat outDetailed)
        => inBase is FileType.Audio or FileType.Video && outDetailed is FileFormat.Txt or FileFormat.Vtt or FileFormat.Srt;

    public Task<bool> CancelAsync(Guid key) {
        if (!_working.TryGetValue(key, out var working)) return Task.FromResult(false);
        try { working.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        return Task.FromResult(true);
    }

    public async Task<ConversionProgress> DoConvertWork(InputFileSource source, FileConversionInfo info) {
        var engine = _engine ?? throw new InvalidOperationException("The converter was not initialized. ");
        if (info.IdWithAdjustment.Adjustment is not FileAdjustmentTranscript adjustment) {
            return new(new FileConversionProgressInfo(FileConversionStatus.Error, 0, 0, "A transcript is asked for with FileAdjustmentTranscript. "));
        }
        var key = info.IdWithAdjustment.GetKey();
        using var cancellation = new CancellationTokenSource();
        var told = new StrongBox<FileConversionProgressInfo?>(null);
        _working[key] = (cancellation, told);
        try {
            var transcript = await engine.ConvertAsync(info, adjustment.AsJson(), source, new Told(told), cancellation.Token);
            var text = TranscriptData.FromBytes(transcript.Bytes).Write(adjustment.RequestedFormat, adjustment.Timestamps);
            return new ConversionProgress(new FileConversionProgressInfo(FileConversionStatus.Ready, 100, 0), new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false));
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            throw;
        } catch (Exception error) {
            return new(new FileConversionProgressInfo(FileConversionStatus.Error, 0, 0, error.Message));
        } finally {
            _working.TryRemove(key, out _);
        }
    }

    sealed class Told(StrongBox<FileConversionProgressInfo?> box) : IProgress<FileConversionProgressInfo> {
        public void Report(FileConversionProgressInfo value) => box.Value = value;
    }

    public bool TryGetLiveStatus(Guid key, [MaybeNullWhen(false)] out FileConversionProgressInfo status) {
        status = _working.TryGetValue(key, out var working) ? working.Told.Value ?? new(FileConversionStatus.InProgress, 0, -1, "Waiting for the transcript") : null;
        return status != null;
    }
    public byte[] CreateStatusResponse(FileFormat requestedFormat, int width, int height, List<string> text, string textColor, string fillColor)
        => TranscriptStatus.Of(requestedFormat, text);
}
