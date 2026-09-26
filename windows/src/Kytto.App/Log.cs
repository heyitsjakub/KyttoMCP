using System.IO;

namespace Kytto.App;

/// <summary>
/// Where a failure goes when there is no console to print it to.
/// </summary>
/// <remarks>
/// A command that throws becomes a failure envelope and the UI renders an error
/// state, which is right for the user and useless for working out why. This is the
/// other half: the exception, in full, beside the app's own data.
/// </remarks>
internal static class Log
{
    private static readonly Lock Gate = new();

    private static string Path
    {
        get
        {
#if DEBUG
            var testData = Environment.GetEnvironmentVariable("KYTTO_TEST_DATA_ROOT");
            if (!string.IsNullOrWhiteSpace(testData))
            {
                return System.IO.Path.Combine(testData, "kytto.log");
            }
#endif
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Kytto",
                "kytto.log");
        }
    }

    internal static void Failure(string context, Exception error) =>
        Write($"{context}: {error.GetType().Name}: {error.Message}\n{error.StackTrace}");

    internal static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Losing a log line must never be what breaks the app.
        }
    }
}
