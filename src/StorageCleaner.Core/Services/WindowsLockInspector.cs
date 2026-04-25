using System.Runtime.InteropServices;
using System.Text;
using StorageCleaner.Core.Abstractions;

namespace StorageCleaner.Core.Services;

public sealed class WindowsLockInspector : ILockInspector
{
    private const int CchRmSessionKey = 32;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;
    private const int ErrorMoreData = 234;
    private const int RmRebootReasonNone = 0;

    public IReadOnlyList<string> TryGetLockingProcesses(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        uint handle = 0;
        var sessionKey = new StringBuilder(CchRmSessionKey + 1);

        try
        {
            var startResult = RmStartSession(out handle, 0, sessionKey);
            if (startResult != 0)
            {
                return [];
            }

            var resources = new[] { path };
            var registerResult = RmRegisterResources(handle, (uint)resources.Length, resources, 0, null, 0, null);
            if (registerResult != 0)
            {
                return [];
            }

            uint processInfoNeeded = 0;
            uint processInfo = 0;
            uint rebootReasons = RmRebootReasonNone;

            var firstListResult = RmGetList(handle, out processInfoNeeded, ref processInfo, null, ref rebootReasons);
            if (firstListResult == 0 && processInfoNeeded == 0)
            {
                return [];
            }

            if (firstListResult != ErrorMoreData)
            {
                return [];
            }

            var processes = new RmProcessInfo[processInfoNeeded];
            processInfo = processInfoNeeded;

            var secondListResult = RmGetList(handle, out processInfoNeeded, ref processInfo, processes, ref rebootReasons);
            if (secondListResult != 0)
            {
                return [];
            }

            return processes
                .Take((int)processInfo)
                .Select(static process =>
                {
                    var appName = string.IsNullOrWhiteSpace(process.AppName) ? "Unknown process" : process.AppName;
                    return $"{appName} (PID {process.Process.dwProcessId})";
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
        finally
        {
            if (handle != 0)
            {
                _ = RmEndSession(handle);
            }
        }
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint dwSessionHandle,
        uint nFiles,
        string[]? rgsFilenames,
        uint nApplications,
        [In] RmUniqueProcess[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private enum RmAppType
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string ServiceShortName;

        public RmAppType ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }
}
