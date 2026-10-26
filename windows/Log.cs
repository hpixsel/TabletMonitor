using System.IO;

namespace TabletMonitor;

public static class Log
{
    private static readonly object Gate = new();
    public static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabletMonitor");
    private static readonly string FilePath = Path.Combine(Directory, "log.txt");

    static Log()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2_000_000)
                File.Move(FilePath, FilePath + ".old", overwrite: true);
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, line + Environment.NewLine); }
            catch { /* ignore */ }
        }
    }
}
