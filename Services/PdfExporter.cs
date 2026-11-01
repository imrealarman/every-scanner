using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace SScanner.Services;

public static class PdfExporter
{
    private const double PageWidthPoints = 595.28; // A4 width; height follows each scan's aspect ratio

    /// <summary>
    /// Writes the pages into one PDF in <paramref name="folder"/> and returns its path. When <paramref name="text"/> has the
    /// recognised words of each page they go in as an invisible layer, so the PDF can be searched and copied from.
    /// </summary>
    public static string Save(IReadOnlyList<byte[]> pages, string folder, string name, IReadOnlyList<OcrService.Page?>? text = null)
    {
        Directory.CreateDirectory(folder);
        var path = UniquePath(folder, Sanitize(name));

        using var document = new PdfDocument();
        document.Info.Title = Path.GetFileNameWithoutExtension(path);
        Write(document, pages, text, () => document.Save(path));
        return path;
    }

    /// <summary>
    /// Adds the pages to the end of a PDF saved earlier and returns its path. The existing pages are kept as they are
    /// (including their text); the file is replaced only once the new one has been written in full.
    /// </summary>
    public static string Append(string existingPath, IReadOnlyList<byte[]> pages, IReadOnlyList<OcrService.Page?>? text = null)
    {
        var temp = existingPath + ".sscanner-tmp";
        try
        {
            using var existing = PdfReader.Open(existingPath, PdfDocumentOpenMode.Import);
            using var document = new PdfDocument();
            document.Info.Title = string.IsNullOrWhiteSpace(existing.Info.Title) ? Path.GetFileNameWithoutExtension(existingPath) : existing.Info.Title;
            foreach (var page in existing.Pages) document.AddPage(page);
            Write(document, pages, text, () => document.Save(temp));
            File.Replace(temp, existingPath, null);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return existingPath;
    }

    /// <summary>Adds one PDF page per image to <paramref name="document"/>, then calls <paramref name="save"/> while the images are still alive.</summary>
    private static void Write(PdfDocument document, IReadOnlyList<byte[]> pages, IReadOnlyList<OcrService.Page?>? text, Action save)
    {
        var streams = new List<MemoryStream>();
        var images = new List<XImage>();
        try
        {
            var fonts = new Dictionary<int, XFont>();
            for (var number = 0; number < pages.Count; number++)
            {
                var bytes = pages[number];
                // PDFsharp reads the stream's internal buffer, so it must be publicly visible.
                var stream = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
                streams.Add(stream);
                var image = XImage.FromStream(stream);
                images.Add(image);

                var page = document.AddPage();
                page.Width = XUnit.FromPoint(PageWidthPoints);
                page.Height = XUnit.FromPoint(PageWidthPoints * image.PixelHeight / image.PixelWidth);
                using var graphics = XGraphics.FromPdfPage(page);
                graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);

                if (text is not null && number < text.Count && text[number] is { Words.Count: > 0 } recognised)
                {
                    try { DrawTextLayer(graphics, recognised, page.Width.Point, page.Height.Point, fonts); }
                    catch (Exception ex) { Log.Error($"Text layer for page {number + 1}", ex); } // the page stays, just not searchable
                }
            }

            save();
        }
        finally
        {
            foreach (var image in images) image.Dispose();
            foreach (var stream in streams) stream.Dispose();
        }
    }

    /// <summary>The text layer is invisible, so every request is answered with Arial (it has Persian, Arabic and Latin letters).</summary>
    private sealed class ArialResolver : IFontResolver
    {
        private static readonly string[] Candidates = ["arial.ttf", "tahoma.ttf", "segoeui.ttf"];

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) => new("text-layer");

        public byte[]? GetFont(string faceName)
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            var file = Candidates.Select(c => Path.Combine(fonts, c)).First(File.Exists);
            return File.ReadAllBytes(file);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex RightToLeftRun = new("[֐-ࣿיִ-﷿ﹰ-﻿]+");

    /// <summary>
    /// PDFsharp reorders right-to-left text into visual order before writing it, which would store Persian words backwards
    /// (and break searching). Reversing those runs first makes it write them back in reading order.
    /// </summary>
    private static string ForPdfsharp(string text) =>
        RightToLeftRun.Replace(text, m => string.Create(m.Length, m.Value, (span, value) => { for (var i = 0; i < span.Length; i++) span[i] = value[^(i + 1)]; }));

    private static bool fontsReady;

    /// <summary>
    /// Writes each word fully transparent at the place it was found, stretched to the word's width, so selecting or
    /// searching in a PDF viewer lands on the printed word. Text is stored in reading (logical) order.
    /// </summary>
    private static void DrawTextLayer(XGraphics graphics, OcrService.Page recognised, double width, double height, Dictionary<int, XFont> fonts)
    {
        if (!fontsReady)
        {
            GlobalFontSettings.FontResolver ??= new ArialResolver();
            fontsReady = true;
        }
        var invisible = new XSolidBrush(XColor.FromArgb(0, 0, 0, 0));
        foreach (var word in recognised.Words)
        {
            var size = (int)Math.Clamp(Math.Round(word.Height * height * 0.8), 3, 72);
            if (!fonts.TryGetValue(size, out var font))
                fonts[size] = font = new XFont("Arial", size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));

            var text = ForPdfsharp(word.Text);
            var natural = graphics.MeasureString(text, font).Width;
            if (natural <= 0) continue;
            var state = graphics.Save();
            graphics.TranslateTransform(word.X * width, word.Y * height);
            graphics.ScaleTransform(Math.Clamp(word.Width * width / natural, 0.2, 5), 1);
            graphics.DrawString(text, font, invisible, 0, 0, XStringFormats.TopLeft);
            graphics.Restore(state);
        }
    }

    public static string Sanitize(string name)
    {
        var cleaned = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).TrimEnd('.', ' ');
        if (cleaned.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[..^4];
        return cleaned.Length == 0 ? "Scan" : cleaned;
    }

    private static string UniquePath(string folder, string baseName)
    {
        var path = Path.Combine(folder, baseName + ".pdf");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{baseName} ({n}).pdf");
        return path;
    }
}
