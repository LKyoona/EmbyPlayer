namespace EmbyRemotePlayer.Services;

public static class LogService
{
    private static readonly object Gate = new();
    private static readonly string LogFile;

    static LogService()
    {
        AppPaths.EnsureDirectories();
        LogFile = Path.Combine(AppPaths.LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warning(string message, Exception? exception = null) => Write("WARN", message, exception);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(level).Append("] ")
                .AppendLine(message);
            if (exception is not null)
            {
                line.AppendLine(exception.ToString());
            }

            lock (Gate)
            {
                File.AppendAllText(LogFile, line.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the application.
        }
    }
}
