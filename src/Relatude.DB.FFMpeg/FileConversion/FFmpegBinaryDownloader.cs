using Relatude.DB.Common;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Relatude.DB.FileConversion;

/// <summary>
/// Downloads the official ffmpeg and ffprobe binaries from ffbinaries.com into a local folder.
/// Replaces the Xabe.FFmpeg.Downloader dependency: same API endpoint and version.json marker,
/// so folders populated by the old downloader are reused without re-downloading.
/// </summary>
internal static class FFmpegBinaryDownloader {
    const string _latestVersionUrl = "https://ffbinaries.com/api/v1/version/latest";
    // was 3 attempts at 2 s then 4 s; the shared cadence covers the same ground inside this budget
    static readonly TimeSpan _downloadRetryTimeout = TimeSpan.FromSeconds(30);
    static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>
    /// Ensures ffmpeg and ffprobe exist in binDir, downloading or upgrading them if needed.
    /// Progress callback: (binary name, downloaded bytes, total bytes or 0 if unknown).
    /// </summary>
    public static async Task EnsureAsync(string binDir, Action<string, long, long>? progress = null) {
        Directory.CreateDirectory(binDir);
        var ffmpegPath = getBinaryPath(binDir, "ffmpeg");
        var ffprobePath = getBinaryPath(binDir, "ffprobe");
        var binariesExist = File.Exists(ffmpegPath) && File.Exists(ffprobePath);
        JsonDocument latest;
        try {
            latest = await getLatestVersionInfoAsync();
        } catch when (binariesExist) {
            return; // offline or api down, keep using existing binaries
        }
        using (latest) {
            var latestVersion = latest.RootElement.GetProperty("version").GetString() ?? "0.0";
            if (binariesExist && !isNewerThanInstalled(binDir, latestVersion)) return;
            var bin = latest.RootElement.GetProperty("bin").GetProperty(getPlatformId());
            await downloadAndExtractAsync(bin.GetProperty("ffmpeg").GetString()!, binDir, "ffmpeg", progress);
            await downloadAndExtractAsync(bin.GetProperty("ffprobe").GetString()!, binDir, "ffprobe", progress);
            makeExecutable(ffmpegPath);
            makeExecutable(ffprobePath);
            File.WriteAllText(Path.Combine(binDir, "version.json"), JsonSerializer.Serialize(new { version = latestVersion }));
        }
    }
    static async Task<JsonDocument> getLatestVersionInfoAsync() {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await _http.GetAsync(_latestVersionUrl, cts.Token);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
    }
    static bool isNewerThanInstalled(string binDir, string latestVersion) {
        try {
            var versionFile = Path.Combine(binDir, "version.json");
            if (!File.Exists(versionFile)) return true;
            using var doc = JsonDocument.Parse(File.ReadAllText(versionFile));
            var installed = doc.RootElement.GetProperty("version").GetString();
            if (!Version.TryParse(installed, out var installedV) || !Version.TryParse(latestVersion, out var latestV)) return true;
            return latestV > installedV;
        } catch {
            return true;
        }
    }
    static async Task downloadAndExtractAsync(string url, string binDir, string name, Action<string, long, long>? progress) {
        var zipTmp = Path.Combine(binDir, Guid.NewGuid().ToString("N") + ".zip.tmp");
        try {
            // the shared cadence, so this download waits on the same rhythm as everything else
            await Retry.RunAsync(() => downloadFileAsync(url, zipTmp, (downloaded, total) => progress?.Invoke(name, downloaded, total)),
                isTransient: ex => ex is HttpRequestException or IOException,
                timeout: _downloadRetryTimeout);
            extractZip(zipTmp, binDir);
        } finally {
            try { if (File.Exists(zipTmp)) File.Delete(zipTmp); } catch { }
        }
    }
    static async Task downloadFileAsync(string url, string filePath, Action<long, long> progress) {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = File.Create(filePath);
        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await contentStream.ReadAsync(buffer)) > 0) {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            downloaded += read;
            progress(downloaded, total);
        }
    }
    static void extractZip(string zipPath, string destinationDir) {
        var fullDest = Path.GetFullPath(destinationDir);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries) {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
            var destPath = Path.GetFullPath(Path.Combine(fullDest, entry.FullName));
            if (!destPath.StartsWith(fullDest, StringComparison.Ordinal)) continue; // zip slip guard
            var dir = Path.GetDirectoryName(destPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }
    static string getBinaryPath(string binDir, string name) => Path.Combine(binDir, OperatingSystem.IsWindows() ? name + ".exe" : name);
    static string getPlatformId() {
        var arch = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows()) {
            if (arch is Architecture.X64 or Architecture.Arm64) return "windows-64"; // arm64 runs the x64 build via emulation
        } else if (OperatingSystem.IsMacOS()) {
            return "osx-64"; // arm64 runs the x64 build via rosetta
        } else if (OperatingSystem.IsLinux()) {
            switch (arch) {
                case Architecture.X64: return "linux-64";
                case Architecture.X86: return "linux-32";
                case Architecture.Arm: return "linux-armhf";
                case Architecture.Arm64: return "linux-arm64";
            }
        }
        throw new PlatformNotSupportedException($"No ffmpeg binaries available for {RuntimeInformation.OSDescription} ({arch}).");
    }
    static void makeExecutable(string path) {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
