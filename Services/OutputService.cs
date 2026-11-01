using OpenCvSharp;

namespace SScanner.Services;

/// <summary>Writes finished pages to disk as a PDF or as individual JPEG/PNG files.</summary>
public static class OutputService
{
    /// <summary>Saves the pages and returns the path of the (first) file written.</summary>
    public static string Save(IReadOnlyList<byte[]> pages, OutputFormat format, int quality, string folder, string name)
    {
        if (format == OutputFormat.Pdf)
            return PdfExporter.Save(pages.AsParallel().AsOrdered().Select(p => ForPdf(p, quality)).ToList(), folder, name);

        Directory.CreateDirectory(folder);
        var extension = format == OutputFormat.Jpeg ? ".jpg" : ".png";
        var baseName = FreeBaseName(folder, PdfExporter.Sanitize(name), extension, pages.Count);

        string first = "";
        for (var i = 0; i < pages.Count; i++)
        {
            var file = pages.Count == 1 ? baseName + extension : $"{baseName}_{i + 1:000}{extension}";
            var path = Path.Combine(folder, file);
            File.WriteAllBytes(path, EncodeImage(pages[i], format, quality));
            if (i == 0) first = path;
        }
        return first;
    }

    /// <summary>Colour and grey pages are re-encoded as JPEG at the chosen quality; black-and-white PNGs stay lossless.</summary>
    public static byte[] ForPdf(byte[] page, int quality) =>
        IsPng(page) ? page : EncodeImage(page, OutputFormat.Jpeg, quality);

    public static byte[] EncodeImage(byte[] page, OutputFormat format, int quality)
    {
        if (format == OutputFormat.Png && IsPng(page)) return page;
        if (format == OutputFormat.Jpeg && !IsPng(page) && quality >= 90) return page; // already JPEG at q90

        using var mat = Cv2.ImDecode(page, ImreadModes.Unchanged);
        return format == OutputFormat.Jpeg
            ? mat.ImEncode(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, Math.Clamp(quality, 30, 100)))
            : mat.ImEncode(".png");
    }

    private static bool IsPng(byte[] data) => data.Length > 4 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G';

    /// <summary>Finds a base name where none of the files we are about to write already exist.</summary>
    private static string FreeBaseName(string folder, string baseName, string extension, int count)
    {
        var candidate = baseName;
        for (var n = 2; Clashes(folder, candidate, extension, count); n++)
            candidate = $"{baseName} ({n})";
        return candidate;
    }

    private static bool Clashes(string folder, string name, string extension, int count) =>
        count == 1
            ? File.Exists(Path.Combine(folder, name + extension))
            : Enumerable.Range(1, count).Any(i => File.Exists(Path.Combine(folder, $"{name}_{i:000}{extension}")));
}
