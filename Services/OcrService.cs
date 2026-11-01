using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace SScanner.Services;

/// <summary>
/// Reads the text on a page with the OCR built into Windows (offline). Persian is read with the Persian recognizer, or the
/// Arabic one when Persian isn't installed (same script, so it covers the Persian letters); English is read alongside,
/// and each page keeps whichever result found more text.
/// </summary>
public static class OcrService
{
    /// <summary>A recognised word; the box is in fractions (0..1) of the page width and height.</summary>
    public sealed record Word(string Text, double X, double Y, double Width, double Height);

    public sealed record Page(IReadOnlyList<Word> Words);

    private static readonly string[][] LanguageGroups =
    [
        ["fa-IR", "fa", "ar-SA", "ar"], // the first one installed is used
        ["en-US", "en-GB", "en"],
    ];

    private static readonly Lazy<IReadOnlyList<OcrEngine>> Engines = new(CreateEngines);
    private static readonly SemaphoreSlim Throttle = new(2); // OCR is CPU heavy; a whole stack at once would stall the app

    /// <summary>False when Windows has no OCR language pack for Persian, Arabic or English.</summary>
    public static bool Available => Engines.Value.Count > 0;

    /// <summary>True when a Persian or Arabic recognizer is installed; without one, Persian text is not read.</summary>
    public static bool PersianAvailable { get; private set; }

    private static IReadOnlyList<OcrEngine> CreateEngines()
    {
        var engines = new List<OcrEngine>();
        try
        {
            foreach (var group in LanguageGroups)
            {
                var engine = group.Select(tag => OcrEngine.TryCreateFromLanguage(new Language(tag))).FirstOrDefault(e => e is not null);
                if (engine is null) continue;
                engines.Add(engine);
                if (group == LanguageGroups[0]) PersianAvailable = true;
            }
            if (engines.Count == 0 && OcrEngine.TryCreateFromUserProfileLanguages() is { } fallback) engines.Add(fallback);
        }
        catch (Exception ex)
        {
            Log.Error("OCR setup", ex);
        }
        return engines;
    }

    /// <summary>Recognises the text on one page image; null if OCR is unavailable or the page could not be read.</summary>
    public static async Task<Page?> RecognizeAsync(byte[] image)
    {
        if (!Available) return null;
        await Throttle.WaitAsync();
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(image.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);

            // The recognizer refuses very large images; boxes are fractions, so shrinking loses nothing later.
            var scale = Math.Min(1.0, OcrEngine.MaxImageDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var width = Math.Max(1, (uint)(decoder.PixelWidth * scale));
            var height = Math.Max(1, (uint)(decoder.PixelHeight * scale));
            var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);

            Page? best = null;
            var bestLength = 0;
            foreach (var engine in Engines.Value)
            {
                var result = await engine.RecognizeAsync(bitmap);
                var words = result.Lines
                    .SelectMany(line => line.Words)
                    .Where(w => w.Text.Trim().Length > 0)
                    .Select(w => new Word(w.Text, w.BoundingRect.X / width, w.BoundingRect.Y / height, w.BoundingRect.Width / width, w.BoundingRect.Height / height))
                    .ToList();
                var length = words.Sum(w => w.Text.Length);
                if (length > bestLength) { best = new Page(words); bestLength = length; }
            }
            return best;
        }
        catch (Exception ex)
        {
            Log.Error("OCR", ex);
            return null;
        }
        finally { Throttle.Release(); }
    }
}
