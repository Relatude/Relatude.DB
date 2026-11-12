using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace Relatude.DB.Common;

/// <summary>
/// Says who holds a file that cannot be opened, so a lock can be told apart: this very process (a
/// handle left open by an earlier open or close that failed), another process on this machine (the
/// previous one still stopping), or none here at all (another machine on a shared folder, or a handle
/// the file server keeps for a client that went away). Windows only, through the Restart Manager;
/// null anywhere it cannot tell.
/// </summary>
public static class FileLockHolders {

    public static string? Describe(string path) => Describe(path, out _);

    /// <param name="path"></param>
    /// <param name="explained">True when the answer explains the lock on its own - this process, or no
    /// process on this machine - so a hint about a previous process still stopping would mislead.</param>
    public static string? Describe(string path, out bool explained) {
        explained = false;
        if (!OperatingSystem.IsWindows()) return null;
        try {
            var holders = query(path);
            if (holders == null) return null;
            if (holders.Count == 0) {
                explained = true;
                return "No process on this machine holds it, so it is held from another machine sharing the folder: another instance "
                    + "of the application, or one that stopped without closing it, whose handle the file server keeps until it times out. ";
            }
            var self = Environment.ProcessId;
            var sb = new StringBuilder();
            foreach (var (pid, name, startedUtc) in holders) {
                if (sb.Length > 0) sb.Append(", ");
                if (pid == self) sb.Append("THIS process (" + name + ", pid " + pid + ")");
                else sb.Append(name + " (pid " + pid + (startedUtc == null ? "" : ", started " + startedUtc.Value.ToString("yyyy-MM-dd HH:mm:ss") + " UTC") + ")");
            }
            var text = "Held by " + sb + ". ";
            if (holders.Any(h => h.Pid == self)) {
                explained = true;
                text += "A handle this process left open is not released by waiting for another process. ";
            }
            return text;
        } catch {
            return null; // a diagnostic must never be the reason an open fails
        }
    }

    static List<(int Pid, string Name, DateTime? StartedUtc)>? query(string path) {
        var key = new StringBuilder(CCH_RM_SESSION_KEY + 1);
        if (RmStartSession(out var session, 0, key) != 0) return null;
        try {
            if (RmRegisterResources(session, 1, [path], 0, null, 0, null) != 0) return null;
            uint count = 0;
            uint reasons = 0;
            RM_PROCESS_INFO[]? infos = null;
            var listed = false;
            for (var attempt = 0; attempt < 3 && !listed; attempt++) {
                var result = RmGetList(session, out var needed, ref count, infos, ref reasons);
                if (result == 0) listed = true;
                else if (result != ERROR_MORE_DATA) return null;
                else {
                    count = needed;
                    infos = new RM_PROCESS_INFO[needed];
                }
            }
            if (!listed) return null;
            var list = new List<(int, string, DateTime?)>();
            for (var i = 0; i < count && infos != null && i < infos.Length; i++) {
                var info = infos[i];
                var pid = info.Process.dwProcessId;
                DateTime? started = null;
                try {
                    var ft = ((long)info.Process.ProcessStartTime.dwHighDateTime << 32) | (uint)info.Process.ProcessStartTime.dwLowDateTime;
                    if (ft > 0) started = DateTime.FromFileTimeUtc(ft);
                } catch { }
                var name = info.strAppName;
                try { name = Process.GetProcessById(pid).ProcessName; } catch { }
                list.Add((pid, string.IsNullOrEmpty(name) ? "unknown" : name, started));
            }
            return list;
        } finally {
            RmEndSession(session);
        }
    }

    const int CCH_RM_SESSION_KEY = 32;
    const int CCH_RM_MAX_APP_NAME = 255;
    const int CCH_RM_MAX_SVC_NAME = 63;
    const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications,
        [In] RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);
}
