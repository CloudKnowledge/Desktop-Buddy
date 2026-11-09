using System;
using System.Diagnostics;
using System.IO;

namespace Mochi.Mac;

/// <summary>Tiny local log in ~/Library/Logs/Mochi (no telemetry). Rotates at 1 MB.</summary>
internal static class Log
{
    static readonly object Gate = new();
    public static string Directory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Mochi");
    static string FilePath => Path.Combine(Directory, "mochi.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
