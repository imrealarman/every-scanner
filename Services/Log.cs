using System.Text;

namespace SScanner.Services;

/// <summary>
/// Plain-text log, one file per day (logs\scanner-2026-10-27.txt) next to the app. If that folder can't be
/// written to (for example the app is installed for all users), it falls back to %AppData%\SScanner\logs.
/// </summary>
public static class Log
{
    private const int KeepDays = 30;
    private static readonly object Gate = new();
    private static string? folder;

    public static string Folder
    {
        get
        {
            lock (Gate) return folder ??= Resolve();
        }
    }

    /// <summary>Today's log file (it may not exist yet).</summary>
    public static string CurrentFile => Path.Combine(Folder, $"scanner-{DateTime.Now:yyyy-MM-dd}.txt");

    public static void Info(string message) => Append("INFO ", message);

    public static void Error(string context, Exception ex)
    {
        var text = new StringBuilder($"{context} failed: {ex.GetType().Name} (0x{ex.HResult:X8}) {ex.Message}");
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            text.Append($"{Environment.NewLine}  caused by {inner.GetType().Name} (0x{inner.HResult:X8}) {inner.Message}");
        if (ex.StackTrace is { } stack) text.Append(Environment.NewLine).Append(stack);
        Append("ERROR", text.ToString());
    }

    private static void Append(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                folder ??= Resolve();
                var file = Path.Combine(folder, $"scanner-{DateTime.Now:yyyy-MM-dd}.txt");
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // logging must never break the app
    }

    private static string Resolve()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "logs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SScanner", "logs"),
        };
        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                Prune(candidate);
                return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return candidates[^1];
    }

    private static void Prune(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "scanner-*.txt"))
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-KeepDays)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
