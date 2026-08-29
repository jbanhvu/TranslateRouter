namespace MeetingInterpreter.Services;

public sealed class AppLogger
{
    private readonly object _syncRoot = new();
    private readonly string _logPath;

    public AppLogger()
    {
        _logPath = ResolveLogPath();
    }

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? exception = null)
    {
        var safeMessage = exception is null
            ? message
            : $"{message} ({exception.GetType().Name}: {exception.Message})";

        Write("ERROR", safeMessage);
    }

    private void Write(string level, string message)
    {
        try
        {
            lock (_syncRoot)
            {
                File.AppendAllText(_logPath, $"{DateTime.Now:O} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    private static string ResolveLogPath()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MeetingInterpreter",
                "Logs"),
            Path.Combine(AppContext.BaseDirectory, "Logs"),
            Path.Combine(Path.GetTempPath(), "MeetingInterpreter", "Logs")
        };

        foreach (var directory in candidates)
        {
            try
            {
                Directory.CreateDirectory(directory);
                return Path.Combine(directory, $"meeting-interpreter-{DateTime.Now:yyyyMMdd}.log");
            }
            catch
            {
            }
        }

        return Path.Combine(Path.GetTempPath(), $"meeting-interpreter-{DateTime.Now:yyyyMMdd}.log");
    }
}
