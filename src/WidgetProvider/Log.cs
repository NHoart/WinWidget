using System.Diagnostics;
using System.IO;
using System.Text;

namespace CustomWidgets.WidgetProvider;

/// <summary>
/// Minimal file logger. A packaged, windowless app has no console, so this is the
/// only practical way to diagnose activation and update failures.
/// </summary>
internal static class Log
{
    private const long MaxLogBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static string _path = Path.Combine(Path.GetTempPath(), "CustomWidgets.WidgetProvider.log");

    public static void Initialize()
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CustomWidgets",
                "logs");
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "provider.log");
        }
        catch
        {
            // Falling back to the temp folder is fine.
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");
    }

    private static void Write(string level, string message)
    {
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
        Debug.WriteLine(line);

        try
        {
            lock (Gate)
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxLogBytes)
                {
                    File.Move(_path, _path + ".old", overwrite: true);
                }

                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never break widget callbacks.
        }
    }
}
