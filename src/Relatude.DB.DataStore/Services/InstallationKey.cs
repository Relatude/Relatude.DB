using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Relatude.DB.Common;

/// <summary>
/// Provides a stable identifier for the current installation/environment.
///
/// The identifier is deliberately NOT persisted. It is calculated from
/// characteristics of the machine/VM and the volume containing the data folder.
///
/// Properties:
/// - Stable across application restarts.
/// - Normally stable across OS/application restarts.
/// - Different machines normally produce different IDs.
/// - Copying the data directory to another machine normally produces another ID.
/// - Works on Windows, Linux, macOS and containers.
/// - Does not require network access.
/// - Does not expose the underlying machine identifiers.
/// </summary>
public static class InstallationIdentity {
    private const string AlgorithmVersion = "1";

    private static string? _cachedId;

    /// <summary>
    /// Gets the stable installation identifier.
    ///
    /// The result is a 32-character hexadecimal string (128 bits).
    /// </summary>
    public static string Get() {

        // The calculation is deterministic, so caching is safe.
        if (_cachedId != null)
            return _cachedId;

        var dataFolder = AppContext.BaseDirectory;
        var normalizedPath = Path.GetFullPath(dataFolder);

        var components = new List<string>
        {
            $"version={AlgorithmVersion}"
        };

        AddComponent(components, "machine", GetMachineIdentity());
        AddComponent(components, "system", GetSystemIdentity());
        AddComponent(components, "volume", GetVolumeIdentity(normalizedPath));

        // Include a fallback based on the data folder if all hardware/system
        // identifiers are unavailable.
        //
        // This is deliberately NOT the absolute path. The path itself should
        // not become part of the identity because moving the application
        // should ideally not change the installation ID.
        if (components.Count == 1) {
            AddComponent(
                components,
                "fallback",
                GetFallbackIdentity(normalizedPath));
        }

        var input = string.Join("|", components);

        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(input));

        // 128 bits is already vastly more than sufficient for this purpose.
        _cachedId = Convert.ToHexString(hash[..16]);

        return _cachedId;
    }

    private static void AddComponent(
        List<string> components,
        string name,
        string? value) {
        if (!string.IsNullOrWhiteSpace(value)) {
            components.Add(
                $"{name}={Normalize(value)}");
        }
    }

    private static string Normalize(string value) {
        return value.Trim().ToUpperInvariant();
    }

    // ---------------------------------------------------------------------
    // Machine identity
    // ---------------------------------------------------------------------

    private static string? GetMachineIdentity() {
        if (OperatingSystem.IsWindows())
            return GetWindowsMachineGuid();

        if (OperatingSystem.IsLinux())
            return GetLinuxMachineId();

        if (OperatingSystem.IsMacOS())
            return GetMacMachineId();

        return null;
    }

    private static string? GetWindowsMachineGuid() {
        try {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");

            return key?.GetValue("MachineGuid")?.ToString();
        } catch {
            return null;
        }
    }

    private static string? GetLinuxMachineId() {
        try {
            string[] paths =
            [
                "/etc/machine-id",
                "/var/lib/dbus/machine-id"
            ];

            foreach (var path in paths) {
                if (!File.Exists(path))
                    continue;

                var value = File.ReadAllText(path).Trim();

                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        } catch {
            // Ignore and allow the other identity components to work.
        }

        return null;
    }

    private static string? GetMacMachineId() {
        // macOS does not have an exact equivalent of Linux machine-id.
        //
        // IOPlatformUUID is normally available through ioreg.
        try {
            var psi = new System.Diagnostics.ProcessStartInfo {
                FileName = "/usr/sbin/ioreg",
                Arguments = "-rd1 -c IOPlatformExpertDevice",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);

            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd();

            process.WaitForExit(2000);

            const string key = "\"IOPlatformUUID\" = \"";

            var start = output.IndexOf(key, StringComparison.Ordinal);

            if (start < 0)
                return null;

            start += key.Length;

            var end = output.IndexOf(
                '"',
                start);

            if (end < 0)
                return null;

            return output[start..end];
        } catch {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // System / VM identity
    // ---------------------------------------------------------------------

    private static string? GetSystemIdentity() {
        if (OperatingSystem.IsLinux()) {
            // systemd based systems expose a stable product/system UUID
            // in /sys/class/dmi/id/product_uuid when available.
            return TryReadFirst(
                "/sys/class/dmi/id/product_uuid",
                "/sys/devices/virtual/dmi/id/product_uuid");
        }

        if (OperatingSystem.IsWindows()) {
            // ComputerSystemProduct UUID is particularly useful for VMs.
            return GetWindowsSystemUuid();
        }

        if (OperatingSystem.IsMacOS()) {
            // IOPlatformUUID is already handled by GetMacMachineId().
            return GetMacMachineId();
        }

        return null;
    }

    private static string? GetWindowsSystemUuid() {
        try {
            var psi = new System.Diagnostics.ProcessStartInfo {
                FileName = "powershell.exe",
                Arguments =
                    "-NoProfile -NonInteractive -Command " +
                    "\"(Get-CimInstance Win32_ComputerSystemProduct).UUID\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);

            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd();

            process.WaitForExit(3000);

            return output.Trim();
        } catch {
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Volume identity
    // ---------------------------------------------------------------------

    private static string? GetVolumeIdentity(string path) {
        if (OperatingSystem.IsWindows())
            return GetWindowsVolumeIdentity(path);

        if (OperatingSystem.IsLinux())
            return GetLinuxVolumeIdentity(path);

        if (OperatingSystem.IsMacOS())
            return GetMacVolumeIdentity(path);

        return null;
    }

    private static string? GetWindowsVolumeIdentity(string path) {
        try {
            var root = Path.GetPathRoot(path);

            if (string.IsNullOrWhiteSpace(root))
                return null;

            var drive = new DriveInfo(root);

            if (!drive.IsReady)
                return null;

            return drive.VolumeLabel + "|" +
                   drive.TotalSize.ToString();
        } catch {
            return null;
        }
    }

    private static string? GetLinuxVolumeIdentity(string path) {
        // The filesystem's device can be discovered without requiring
        // external packages. We deliberately avoid relying on the mount
        // path itself.
        try {
            var mount = FindLinuxMountPoint(path);

            if (mount == null)
                return null;

            var device = TryReadLinuxMountDevice(mount.Value.MountPoint);

            if (device == null)
                return null;

            var uuid = TryGetLinuxBlockDeviceUuid(device);

            return uuid ?? device;
        } catch {
            return null;
        }
    }

    private static string? GetMacVolumeIdentity(string path) {
        try {
            var psi = new System.Diagnostics.ProcessStartInfo {
                FileName = "/usr/sbin/diskutil",
                Arguments = $"info \"{path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);

            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd();

            process.WaitForExit(3000);

            foreach (var line in output.Split('\n')) {
                var trimmed = line.Trim();

                if (trimmed.StartsWith(
                    "Volume UUID:",
                    StringComparison.OrdinalIgnoreCase)) {
                    return trimmed[
                        "Volume UUID:".Length..].Trim();
                }
            }
        } catch {
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Linux helpers
    // ---------------------------------------------------------------------

    private readonly record struct MountInfo(
        string MountPoint,
        string Device);

    private static MountInfo? FindLinuxMountPoint(string path) {
        try {
            path = Path.GetFullPath(path);

            var bestMatch = default(MountInfo?);
            var bestLength = -1;

            foreach (var line in File.ReadLines("/proc/mounts")) {
                var parts = line.Split(' ');

                if (parts.Length < 2)
                    continue;

                var device = UnescapeMountPath(parts[0]);
                var mountPoint = UnescapeMountPath(parts[1]);

                if (!path.StartsWith(
                    mountPoint,
                    StringComparison.Ordinal))
                    continue;

                if (mountPoint.Length > bestLength) {
                    bestLength = mountPoint.Length;

                    bestMatch = new MountInfo(
                        mountPoint,
                        device);
                }
            }

            return bestMatch;
        } catch {
            return null;
        }
    }

    private static string? TryReadLinuxMountDevice(
        string mountPoint) {
        try {
            foreach (var line in File.ReadLines("/proc/mounts")) {
                var parts = line.Split(' ');

                if (parts.Length < 2)
                    continue;

                var device = UnescapeMountPath(parts[0]);
                var mount = UnescapeMountPath(parts[1]);

                if (string.Equals(
                    mount,
                    mountPoint,
                    StringComparison.Ordinal)) {
                    return device;
                }
            }
        } catch {
        }

        return null;
    }

    private static string? TryGetLinuxBlockDeviceUuid(
        string device) {
        try {
            if (!device.StartsWith("/dev/"))
                return null;

            var name = Path.GetFileName(device);

            var uuidPath =
                $"/dev/disk/by-uuid";

            if (!Directory.Exists(uuidPath))
                return null;

            foreach (var link in Directory.GetFiles(uuidPath)) {
                try {
                    var target =
                        Path.GetFullPath(
                            Path.Combine(
                                uuidPath,
                                Path.GetFileName(link)));

                    var resolved =
                        ResolveSymbolicLink(link);

                    if (resolved == null)
                        continue;

                    if (string.Equals(
                        resolved,
                        device,
                        StringComparison.Ordinal)) {
                        return Path.GetFileName(link);
                    }
                } catch {
                }
            }
        } catch {
        }

        return null;
    }

    private static string? ResolveSymbolicLink(string path) {
        try {
            var info = new FileInfo(path);

            var target = info.ResolveLinkTarget(true);

            return target?.FullName;
        } catch {
            return null;
        }
    }

    private static string UnescapeMountPath(string value) {
        return value
            .Replace("\\040", " ")
            .Replace("\\011", "\t")
            .Replace("\\012", "\n")
            .Replace("\\134", "\\");
    }

    // ---------------------------------------------------------------------
    // Generic helpers
    // ---------------------------------------------------------------------

    private static string? TryReadFirst(params string[] paths) {
        foreach (var path in paths) {
            try {
                if (!File.Exists(path))
                    continue;

                var value = File.ReadAllText(path).Trim();

                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            } catch {
            }
        }

        return null;
    }

    private static string GetFallbackIdentity(string dataFolder) {
        // This fallback is intentionally weaker than the normal identity.
        //
        // We include the machine name and normalized data folder so that
        // completely restricted environments still get a deterministic ID.
        //
        // It is not intended to provide strong uniqueness.
        return $"{Environment.MachineName}|{dataFolder}";
    }
}

