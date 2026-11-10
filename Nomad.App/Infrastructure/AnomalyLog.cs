using System.Diagnostics;

namespace Nomad.App.Infrastructure;

/// <summary>
/// Notes states that should never happen (a session listed twice, a session's id changing under it) to
/// %APPDATA%\Nomad\anomalies.log, with the stack that got there, so a bug seen once can be traced afterwards.
/// </summary>
public static class AnomalyLog
{
    /// <summary>
    /// Past this the log starts over, so it never grows without bound.
    /// </summary>
    private const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// Serialises the writes (the log is appended to from the UI thread and from sessions' events).
    /// </summary>
    private static readonly Lock Gate = new();

    /// <summary>
    /// The log file.
    /// </summary>
    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nomad", "anomalies.log");

    /// <summary>
    /// Appends what happened, with the time, the app's version and the current stack.
    /// </summary>
    public static void Write(string what)
    {
        var version = typeof(AnomalyLog).Assembly.GetName().Version?.ToString(3);
        var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} (v{version}) {what}{Environment.NewLine}{new StackTrace(1, false)}{Environment.NewLine}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    File.Delete(FilePath);
                }

                File.AppendAllText(FilePath, entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that can't be written is no reason to fail what is being logged.
        }
    }
}
