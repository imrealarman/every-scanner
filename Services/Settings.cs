using System.Text.Json;
using System.Text.Json.Serialization;

namespace SScanner.Services;

public enum AppTheme { System, Light, Dark }
public enum OutputFormat { Pdf, Jpeg, Png }
public enum DateStyle { Shamsi, Gregorian }

/// <summary>
/// User preferences, persisted as JSON under %AppData%\SScanner. The same shape is used for
/// workspaces (named, exportable presets), which carry everything except the theme.
/// </summary>
public sealed class Settings
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SScanner", "settings.json");

    public string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Scans");

    /// <summary>WIA id of the chosen scanner; empty means automatic. Tied to this PC, so workspaces don't carry it.</summary>
    public string ScannerId { get; set; } = "";

    public PaperSize Paper { get; set; } = PaperSize.Auto;

    /// <summary>Use the scanner's own TWAIN driver when it has one: it feeds a whole stack in one pass, far faster than WIA.</summary>
    public bool UseTwain { get; set; } = true;

    public ScanSource Source { get; set; } = ScanSource.Auto;
    public ScanColor Color { get; set; } = ScanColor.Color;
    public int Dpi { get; set; } = 300;
    public bool SkipBlank { get; set; } = true;
    public bool Fast { get; set; }

    /// <summary>After each scan or import, check every page for readability and pick its brightness, highlights, shadows and grayscale look.</summary>
    public bool SmartMode { get; set; }
    public bool DoubleSided { get; set; }

    /// <summary>Check every new page for tilt and crooked edges and fix them automatically.</summary>
    public bool AutoStraighten { get; set; } = true;

    public OutputFormat Format { get; set; } = OutputFormat.Pdf;

    /// <summary>JPEG quality (also used for photos/colour pages inside a PDF), 30..100.</summary>
    public int Quality { get; set; } = 85;

    /// <summary>Read the text of each page when saving a PDF and add it as an invisible layer, so the PDF is searchable.</summary>
    public bool Ocr { get; set; } = true;

    public bool DailyFolders { get; set; }
    public DateStyle DateStyle { get; set; } = DateStyle.Shamsi;

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>Name of the workspace these settings were loaded from or saved to.</summary>
    public string Workspace { get; set; } = "";

    /// <summary>The folder files are saved into today: the base folder, plus a dated subfolder when enabled.</summary>
    public string TargetFolder(DateTime now) =>
        DailyFolders ? Path.Combine(Folder, DailyFolder.Name(DateStyle, now)) : Folder;

    /// <summary>Copies the workspace-level preferences. Theme and workspace name stay as they are.</summary>
    public void CopyFrom(Settings other)
    {
        Folder = other.Folder;
        Paper = other.Paper;
        UseTwain = other.UseTwain;
        Source = other.Source;
        Color = other.Color;
        Dpi = other.Dpi;
        SkipBlank = other.SkipBlank;
        Fast = other.Fast;
        SmartMode = other.SmartMode;
        DoubleSided = other.DoubleSided;
        AutoStraighten = other.AutoStraighten;
        Format = other.Format;
        Quality = Math.Clamp(other.Quality, 30, 100);
        Ocr = other.Ocr;
        DailyFolders = other.DailyFolders;
        DateStyle = other.DateStyle;
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (IOException) { }
    }
}
