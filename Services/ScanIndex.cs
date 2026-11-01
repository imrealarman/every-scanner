namespace SScanner.Services;

/// <summary>A saved scan shown in search results. <see cref="None"/> is the "nothing found" placeholder.</summary>
public sealed record FileHit(string Name, string Detail, string? Path)
{
    public static FileHit None => new("No matching scans", "", null);
}

/// <summary>
/// In-memory index of saved scans under the scan folder, searched as you type (like Everything).
/// Rebuilt in the background whenever the search box is focused or something is saved.
/// </summary>
public sealed class ScanIndex
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".jpg", ".jpeg", ".png" };
    private const int MaxFiles = 200_000;

    private sealed record Entry(string Path, string Name, string Folder, string Haystack, DateTime Modified, long Size);

    private IReadOnlyList<Entry> entries = [];

    public Task RefreshAsync(string root) => Task.Run(() =>
    {
        var found = new List<Entry>();
        try
        {
            if (Directory.Exists(root))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (var info in new DirectoryInfo(root).EnumerateFiles("*", options))
                {
                    if (!Extensions.Contains(info.Extension)) continue;
                    var folder = System.IO.Path.GetRelativePath(root, info.DirectoryName ?? root);
                    folder = folder == "." ? "" : folder;
                    found.Add(new Entry(info.FullName, info.Name, folder, (folder + "\\" + info.Name).ToLowerInvariant(), info.LastWriteTime, info.Length));
                    if (found.Count >= MaxFiles) break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        found.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        entries = found;
    });

    /// <summary>Files whose folder or name contains every word of <paramref name="query"/>, newest first.</summary>
    public List<FileHit> Search(string query, int max = 40)
    {
        var words = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return entries
            .Where(e => words.All(w => e.Haystack.Contains(w)))
            .Take(max)
            .Select(e => new FileHit(
                e.Name,
                $"{(e.Folder.Length == 0 ? "Scans folder" : e.Folder)}  ·  {e.Modified:yyyy-MM-dd HH:mm}  ·  {FormatSize(e.Size)}",
                e.Path))
            .ToList();
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
