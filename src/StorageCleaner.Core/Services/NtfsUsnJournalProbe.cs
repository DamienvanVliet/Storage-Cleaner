using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace StorageCleaner.Core.Services;

internal static class NtfsUsnJournalProbe
{
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;

    public static bool TryCheck(string rootPath, out string detail)
    {
        detail = "Unknown fast-scan state.";
        if (!TryGetVolumeRoot(rootPath, out var volumeRoot, out var error))
        {
            detail = error;
            return false;
        }

        if (!TryGetFileSystemName(volumeRoot, out var fsName, out error))
        {
            detail = error;
            return false;
        }

        if (!string.Equals(fsName, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            detail = $"Fast mode unavailable for filesystem '{fsName}'.";
            return false;
        }

        var devicePath = @"\\.\" + volumeRoot.TrimEnd('\\');
        using var handle = CreateFile(
            devicePath,
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var win32 = Marshal.GetLastWin32Error();
            detail = $"Unable to open NTFS volume for USN probe ({win32}).";
            return false;
        }

        if (!DeviceIoControl(
                handle,
                FsctlQueryUsnJournal,
                IntPtr.Zero,
                0,
                out USN_JOURNAL_DATA_V0 journal,
                Marshal.SizeOf<USN_JOURNAL_DATA_V0>(),
                out _,
                IntPtr.Zero))
        {
            var win32 = Marshal.GetLastWin32Error();
            detail = $"USN journal query failed ({win32}). Falling back to standard scan.";
            return false;
        }

        detail = $"USN journal available (ID={journal.UsnJournalID}, NextUSN={journal.NextUsn}).";
        return true;
    }

    private static bool TryGetVolumeRoot(string path, out string volumeRoot, out string error)
    {
        volumeRoot = string.Empty;
        error = string.Empty;

        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                error = "Unable to determine volume root for fast scan.";
                return false;
            }

            volumeRoot = root;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Fast scan probe path normalization failed: {ex.Message}";
            return false;
        }
    }

    private static bool TryGetFileSystemName(string rootPath, out string fileSystemName, out string error)
    {
        fileSystemName = string.Empty;
        error = string.Empty;
        var fsBuilder = new System.Text.StringBuilder(32);

        var ok = GetVolumeInformation(
            rootPath,
            null,
            0,
            out _,
            out _,
            out _,
            fsBuilder,
            fsBuilder.Capacity);

        if (!ok)
        {
            var win32 = Marshal.GetLastWin32Error();
            error = $"Unable to read filesystem information ({win32}).";
            return false;
        }

        fileSystemName = fsBuilder.ToString();
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct USN_JOURNAL_DATA_V0
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint desiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        out USN_JOURNAL_DATA_V0 lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        System.Text.StringBuilder? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        System.Text.StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);
}
