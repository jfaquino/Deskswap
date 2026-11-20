using System.Globalization;

namespace Deskswap.App;

/// <summary>A minimal append-only log in the data folder, rotated at 1 MB. Never throws.</summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    /// <summary>Writes an informational line.</summary>
    /// <param name="message">The message.</param>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>Writes an error with its exception.</summary>
    /// <param name="message">What was happening.</param>
    /// <param name="exception">The exception.</param>
    public static void Error(string message, Exception exception) => Write("ERROR", $"{message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                var file = new FileInfo(AppPaths.Log);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(file.FullName, file.FullName + ".old", overwrite: true);
                }

                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                File.AppendAllText(AppPaths.Log, $"{stamp} {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never break the app.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}
