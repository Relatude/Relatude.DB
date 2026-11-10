namespace Relatude.DB.FileConversion.FFMpeg;

/// <summary>
/// ffmpeg and ffprobe for code outside the store, such as an editor of videos: the same binaries the store converts
/// videos with (<see cref="NativeVideoConverter"/>), downloaded on first use into the application's ffmpeg folder, else
/// the ones on the PATH. Arguments are passed one by one, so paths need no quoting.
/// </summary>
public static class FFmpeg {
    static readonly string _binaryFolder = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
    static readonly SemaphoreSlim _downloadLock = new(1, 1);
    static volatile bool _ready;
    static volatile string? _status;

    /// <summary>Whether the binaries have been made ready (<see cref="EnsureAsync"/>).</summary>
    public static bool IsReady => _ready;

    /// <summary>What getting the binaries is doing, for people: the download's progress, or why it failed.</summary>
    public static string? Status => _status;

    /// <summary>
    /// Makes sure ffmpeg and ffprobe are there, downloading them the first time (or a newer version), at most one
    /// download at a time. Never throws: when the download fails, <see cref="Status"/> says why, the binaries on the PATH
    /// are used, and the next call tries again. True when the downloaded binaries are ready.
    /// </summary>
    public static async Task<bool> EnsureAsync() {
        if (_ready) return true;
        await _downloadLock.WaitAsync();
        try {
            if (_ready) return true;
            FFmpegProcess.BinaryFolder = _binaryFolder; // its binaries when there, else the ones on the PATH
            Directory.CreateDirectory(_binaryFolder);
            _status = "Downloading FFmpeg...";
            await FFmpegBinaryDownloader.EnsureAsync(_binaryFolder,
                (name, downloadedBytes, totalBytes) => _status = totalBytes > 0
                    ? $"Downloading {name}: {downloadedBytes / 1024} KB / {totalBytes / 1024} KB"
                    : $"Downloading {name}: {downloadedBytes / 1024} KB");
            _ready = true;
            _status = null;
        } catch (Exception ex) {
            _status = "Error downloading FFmpeg: " + ex.Message;
        } finally {
            _downloadLock.Release();
        }
        return _ready;
    }

    /// <summary>
    /// Runs ffmpeg with arguments that name the input and the output; an output already there is overwritten. Given the
    /// duration of the output and a callback, it reports how far it has come, as a percentage. Cancelling kills ffmpeg
    /// and throws <see cref="OperationCanceledException"/>; a run that fails throws with the last lines ffmpeg wrote.
    /// </summary>
    public static async Task RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default, TimeSpan? duration = null, Action<double>? onPercent = null) {
        await EnsureAsync();
        await FFmpegProcess.RunAsync(arguments, cancellation, duration, onPercent);
    }

    /// <summary>What ffprobe tells of a media file: how long it lasts, its picture's size as shown, its frame rate, whether it has sound.</summary>
    public static async Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellation = default) {
        await EnsureAsync();
        return await FFmpegProcess.ProbeAsync(path, cancellation);
    }
}
