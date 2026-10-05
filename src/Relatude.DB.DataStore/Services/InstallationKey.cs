using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Relatude.DB.Common;

/// <summary>
/// Provides a stable identifier for the host the application runs on.
///
/// The identifier is deliberately NOT persisted. On Azure App Service it is
/// calculated from the app's own identity - its subscription, resource group,
/// name and slot - since the worker VM below it is shared by every app of the
/// plan and changes whenever Azure moves the app. Everywhere else it is
/// calculated from characteristics of the machine/VM and the volume the
/// application runs from.
///
/// It tells hosts apart, not applications: two applications on one machine
/// share it. The caller adds what tells those apart.
///
/// Properties:
/// - Stable across application restarts.
/// - Normally stable across OS/application restarts.
/// - Different machines normally produce different IDs.
/// - Different App Service apps and slots produce different IDs.
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

        _cachedId = Calculate(Environment.GetEnvironmentVariable);

        return _cachedId;
    }

    /// <summary>
    /// What <see cref="Get"/> is calculated from, for people: the Azure App
    /// Service app and slot, or this machine.
    /// </summary>
    public static string Describe() => Describe(Environment.GetEnvironmentVariable);

    internal static string Describe(Func<string, string?> environment) {
        var appService = GetAzureAppService(environment);
        if (appService == null)
            return "this machine";
        return $"Azure App Service app \"{appService.Value.Site}\""
            + (string.IsNullOrWhiteSpace(appService.Value.Slot) ? "" : $", slot {appService.Value.Slot}");
    }

    internal static string Calculate(Func<string, string?> environment) {
        var components = new List<string>
        {
            $"version={AlgorithmVersion}"
        };

        if (GetAzureAppService(environment) is { } appService) {
            AddComponent(components, "appservice-owner", appService.Owner);
            AddComponent(components, "appservice-site", appService.Site);
            AddComponent(components, "appservice-slot", appService.Slot);
        } else {
            var normalizedPath = Path.GetFullPath(AppContext.BaseDirectory);

            AddComponent(components, "machine", GetMachineIdentity());
            AddComponent(components, "system", GetSystemIdentity());
            AddComponent(components, "volume", GetVolumeIdentity(normalizedPath));

            // Include a fallback based on the machine name and the application
            // folder if all hardware/system identifiers are unavailable.
            if (components.Count == 1) {
                AddComponent(
                    components,
                    "fallback",
                    GetFallbackIdentity(normalizedPath));
            }
        }

        var input = string.Join("|", components);

        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(input));

        // 128 bits is already vastly more than sufficient for this purpose.
        return Convert.ToHexString(hash[..16]);
    }

    // ---------------------------------------------------------------------
    // Azure App Service
    // ---------------------------------------------------------------------

    /// <summary>
    /// The app as Azure App Service names it, or null outside App Service.
    /// WEBSITE_OWNER_NAME holds the subscription id and the resource group's
    /// webspace, WEBSITE_SITE_NAME the app (the same for all its slots) and
    /// WEBSITE_SLOT_NAME the slot ("Production" for the main one). All three
    /// stay the same when Azure moves the app to another worker, unlike the
    /// worker's own identifiers, and the worker's are the same for every app of
    /// a plan. WEBSITE_INSTANCE_ID is left out on purpose: it names the worker.
    /// </summary>
    private static (string? Owner, string Site, string? Slot)? GetAzureAppService(Func<string, string?> environment) {
        var site = environment("WEBSITE_SITE_NAME");
        if (string.IsNullOrWhiteSpace(site))
            return null;
        var owner = environment("WEBSITE_OWNER_NAME");
        var slot = environment("WEBSITE_SLOT_NAME");
        return (
            string.IsNullOrWhiteSpace(owner) ? null : owner.Trim(),
            site.Trim(),
            string.IsNullOrWhiteSpace(slot) ? null : slot.Trim());
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

    private static string GetFallbackIdentity(string applicationFolder) {
        // This fallback is intentionally weaker than the normal identity.
        //
        // We include the machine name and normalized application folder so that
        // completely restricted environments still get a deterministic ID.
        //
        // It is not intended to provide strong uniqueness.
        return $"{Environment.MachineName}|{applicationFolder}";
    }
}

