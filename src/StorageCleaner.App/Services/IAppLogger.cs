namespace StorageCleaner.App.Services;

public interface IAppLogger
{
    string LogFilePath { get; }

    void LogInfo(string message);

    void LogWarning(string message);

    void LogError(string message, Exception? exception = null);
}
