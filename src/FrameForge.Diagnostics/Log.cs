using FrameForge.Core;

namespace FrameForge.Diagnostics;

public static class Log
{
    static readonly Lock Gate = new();

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    static void Write(string level, string message, Exception? exception)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}";
            if (exception != null)
                line += Environment.NewLine + exception;

            lock (Gate)
            {
                string path = AppPaths.LogFile;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                {
                    string previous = path + ".1";
                    File.Delete(previous);
                    File.Move(path, previous);
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must not take down a recording.
        }
    }
}
