namespace StorageCleaner.Core.Models;

public enum CleanupCategory
{
    ManualSelection,
    WindowsTemp,
    UserTemp,
    RecycleBin,
    BrowserCache,
    OldLogFiles,
    DuplicateFiles,
    NeverAccessedFiles
}
