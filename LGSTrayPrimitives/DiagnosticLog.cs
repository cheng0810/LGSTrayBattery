using System.Text;

namespace LGSTrayPrimitives;

public static class DiagnosticLog
{
    private const long MaxFileSize = 1024 * 1024;
    private const int ArchiveCount = 3;
    private static readonly object _writeLock = new();
    private static readonly Encoding _encoding = new UTF8Encoding(false);
    private static string? _logPath;

    public static string? LogPath => _logPath;

    public static void Initialize(string processName)
    {
        lock (_writeLock)
        {
            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var logDirectory = Path.Combine(localAppData, "LGSTrayBattery", "logs");
                Directory.CreateDirectory(logDirectory);
                _logPath = Path.Combine(logDirectory, $"{processName}.log");
                RotateIfNeeded();
            }
            catch
            {
                _logPath = null;
            }
        }

        WriteLine($"Logger initialized for {processName}; version={Environment.Version}; pid={Environment.ProcessId}");
    }

    public static void WriteLine(string message)
    {
        lock (_writeLock)
        {
            if (_logPath == null)
            {
                return;
            }

            try
            {
                RotateIfNeeded();
                var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
                File.AppendAllText(_logPath, $"{timestamp} [{Environment.ProcessId}] {message}{Environment.NewLine}", _encoding);
            }
            catch
            {
                // Logging must never terminate the battery service.
            }
        }
    }

    public static void WriteException(string context, Exception exception)
    {
        WriteLine($"{context}: {exception}");
    }

    private static void RotateIfNeeded()
    {
        if (_logPath == null || !File.Exists(_logPath) || new FileInfo(_logPath).Length < MaxFileSize)
        {
            return;
        }

        var oldestArchive = $"{_logPath}.{ArchiveCount}";
        if (File.Exists(oldestArchive))
        {
            File.Delete(oldestArchive);
        }

        for (var archive = ArchiveCount - 1; archive >= 1; archive--)
        {
            var source = $"{_logPath}.{archive}";
            if (File.Exists(source))
            {
                File.Move(source, $"{_logPath}.{archive + 1}");
            }
        }

        File.Move(_logPath, $"{_logPath}.1");
    }
}
