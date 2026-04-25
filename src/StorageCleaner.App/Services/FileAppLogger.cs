using System.Text;
using System.IO;

namespace StorageCleaner.App.Services;

public sealed class FileAppLogger : IAppLogger
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileAppLogger(string? logFilePath = null)
    {
        LogFilePath = logFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StorageCleaner",
            "logs",
            "storage-cleaner.log");
    }

    public string LogFilePath { get; }

    public void LogInfo(string message)
    {
        Write("INFO", message, exception: null);
    }

    public void LogWarning(string message)
    {
        Write("WARN", message, exception: null);
    }

    public void LogError(string message, Exception? exception = null)
    {
        Write("ERROR", message, exception);
    }

    private void Write(string level, string message, Exception? exception)
    {
        var directory = Path.GetDirectoryName(LogFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new StringBuilder();
        builder.Append('[')
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append("] [")
            .Append(level)
            .Append("] [T")
            .Append(Environment.CurrentManagedThreadId)
            .Append("] [P")
            .Append(Environment.ProcessId)
            .Append("] [V")
            .Append(typeof(FileAppLogger).Assembly.GetName().Version?.ToString() ?? "0.0.0")
            .Append("] ")
            .Append(message);

        if (exception is not null)
        {
            builder.AppendLine();
            builder.Append(exception);
        }

        builder.AppendLine();
        var line = builder.ToString();

        _writeLock.Wait();
        try
        {
            File.AppendAllText(LogFilePath, line);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
