using System.Diagnostics;

namespace MixerLive.Core.Logging;

/// <summary>
/// Minimal thread-safe file logger. One file per day under %APPDATA%\MixerLive\logs.
/// </summary>
public static class Log
{
    private static readonly object Sync = new();
    private static StreamWriter? _writer;

    public static string LogDirectory { get; private set; } = "";
    public static string? CurrentFile { get; private set; }

    /// <summary>Raised for every line written (used by the UI log panel).</summary>
    public static event Action<string>? LineWritten;

    public static void Init(string? directory = null)
    {
        try
        {
            directory ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MixerLive", "logs");
            Directory.CreateDirectory(directory);
            LogDirectory = directory;
            CurrentFile = Path.Combine(directory, $"mixerlive-{DateTime.Now:yyyyMMdd}.log");
            var fs = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs) { AutoFlush = true };
            Info($"---- MixerLive started (pid {Environment.ProcessId}, {Environment.OSVersion}) ----");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        lock (Sync)
        {
            try { _writer?.WriteLine(line); } catch { /* ignore */ }
        }
        Debug.WriteLine(line);
        try { LineWritten?.Invoke(line); } catch { /* ignore */ }
    }
}
