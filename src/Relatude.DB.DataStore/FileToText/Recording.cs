using Relatude.DB.Common;
using Relatude.DB.FileConversion;

namespace Relatude.DB.FileToText;

/// <summary>
/// What a recording or the sound of a video is made into before it is sent to be transcribed: one channel at 16 kHz,
/// Ogg Opus at 24 kbps, the encoder tuned for speech. What speech to text needs, at about 11 MB an hour; speech to text
/// hears at 16 kHz in any case. The same file gives the same bytes every time, so the service knows a sound it has read
/// before, and the database keeps it among its converted files under one key a part.
/// </summary>
public static class SpeechSound {
    /// <summary>The speech of the stretch from <paramref name="startMs"/> (the start when null) lasting <paramref name="durationMs"/> (to the end when null).</summary>
    public static FileAdjustmentAudio Adjustment(int? startMs = null, int? durationMs = null) =>
        new() { RequestedFormat = FileFormat.Ogg, Channels = 1, SampleRate = 16_000, BitRateKbps = 24, Speech = true, StartMs = startMs, DurationMs = durationMs };
}

/// <summary>
/// A recording or a video to be transcribed (<see cref="IFileToTextProvider.TranscribeAsync"/>), whose speech is made by
/// whoever holds it: the database makes a stored file's through its cached file conversions, so the sound is taken out
/// once, queued with the other conversions and listed with them, and a second transcript of the same file reads it
/// from the cache. A provider asks for its length, to cut it into the parts its service takes, and for each part's
/// speech (<see cref="SpeechSound"/>).
/// </summary>
public interface IRecording {
    /// <summary>What the file is called, which the service is told and the result gives back.</summary>
    string? FileName { get; }
    /// <summary>The file's own format (MP4, MP3...), which the transcript says it was read from.</summary>
    FileFormat Format { get; }
    /// <summary>What the same recording is known by, for a provider that keeps transcripts: the same for the same bytes. Null when nothing says.</summary>
    string? Identity { get; }
    /// <summary>How long it plays; null when it cannot be told, and it is then sent whole.</summary>
    Task<TimeSpan?> LengthAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// The speech of a stretch of it, in the form <see cref="SpeechSound.Adjustment"/> makes: from <paramref name="start"/>
    /// (the start when null) for <paramref name="duration"/> (to the end when null). <paramref name="progress"/> is told how
    /// far its conversion has come; its <see cref="FileConversionProgressInfo.Message"/> only when there is more to say than
    /// that the sound is being taken out, such as that it waits for the converter.
    /// </summary>
    Task<RecordingSpeech> SpeechAsync(TimeSpan? start, TimeSpan? duration, IProgress<FileConversionProgressInfo>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>A stretch of a recording's speech, and whether it was kept from before rather than taken out now.</summary>
public sealed record RecordingSpeech(byte[] Sound, bool Kept);
