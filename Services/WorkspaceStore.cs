using System.Text.Json;

namespace SScanner.Services;

/// <summary>Named presets of scan/output settings, one JSON file each under %AppData%\SScanner\Workspaces.</summary>
public static class WorkspaceStore
{
    public const string Extension = ".sscanws";

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SScanner", "Workspaces");

    public static List<string> List()
    {
        if (!Directory.Exists(Dir)) return [];
        return Directory.EnumerateFiles(Dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static Settings? Load(string name)
    {
        try { return Parse(File.ReadAllText(PathFor(name))); }
        catch (IOException) { return null; }
    }

    /// <summary>Writes a copy of <paramref name="settings"/> under <paramref name="name"/>.</summary>
    public static void Save(string name, Settings settings)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathFor(name), Serialize(settings, name));
    }

    public static void Delete(string name)
    {
        try { File.Delete(PathFor(name)); } catch (IOException) { }
    }

    /// <summary>The JSON stored for a workspace and written by Export.</summary>
    public static string Serialize(Settings settings, string name)
    {
        var copy = new Settings { Workspace = name };
        copy.CopyFrom(settings);
        return JsonSerializer.Serialize(copy, Settings.Json);
    }

    /// <summary>Parses workspace JSON; returns null if it isn't a valid workspace.</summary>
    public static Settings? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<Settings>(json, Settings.Json); }
        catch (JsonException) { return null; }
    }

    public static string UniqueName(string wanted)
    {
        var baseName = PdfExporter.Sanitize(wanted);
        var existing = new HashSet<string>(List(), StringComparer.CurrentCultureIgnoreCase);
        var name = baseName;
        for (var n = 2; existing.Contains(name); n++) name = $"{baseName} ({n})";
        return name;
    }

    private static string PathFor(string name) => Path.Combine(Dir, PdfExporter.Sanitize(name) + ".json");
}
