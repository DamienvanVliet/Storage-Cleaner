using System.Runtime.InteropServices;
using StorageCleaner.Core.Abstractions;

namespace StorageCleaner.Core.Services;

public sealed class WindowsRebootDeletionScheduler : IRebootDeletionScheduler
{
    private const int MoveFileDelayUntilReboot = 0x00000004;

    public bool TryScheduleDelete(string path, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            errorMessage = "Path is empty.";
            return false;
        }

        try
        {
            var normalized = Path.GetFullPath(path);
            if (!File.Exists(normalized) && !Directory.Exists(normalized))
            {
                errorMessage = "Path does not exist.";
                return false;
            }

            if (MoveFileEx(normalized, null, MoveFileDelayUntilReboot))
            {
                return true;
            }

            var code = Marshal.GetLastWin32Error();
            errorMessage = $"MoveFileEx failed with Win32 error {code}.";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);
}
