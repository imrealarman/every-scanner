using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SScanner.Services;

/// <summary>Turns the pages of an existing PDF into images (with the PDF reader built into Windows) so they can be edited like scans.</summary>
public static class PdfImporter
{
    private const double Dpi = 200;
    private const uint MaxSide = 4000; // keeps huge poster pages from using gigabytes

    /// <summary>Renders every page of the PDF to a PNG, in order. Throws a readable error for locked or damaged files.</summary>
    public static async Task<List<byte[]>> RenderAsync(string path, IProgress<(int Done, int Total)>? progress = null)
    {
        PdfDocument document;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            document = await PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception ex)
        {
            Log.Error("PDF import", ex);
            throw new InvalidOperationException($"Could not open “{Path.GetFileName(path)}”. It may be damaged.");
        }
        if (document.IsPasswordProtected)
            throw new InvalidOperationException($"“{Path.GetFileName(path)}” is password protected. Remove the password first.");

        var pages = new List<byte[]>();
        for (uint i = 0; i < document.PageCount; i++)
        {
            using var page = document.GetPage(i);
            var scale = Dpi / 96.0; // page sizes are in 1/96 inch
            var width = page.Size.Width * scale;
            var height = page.Size.Height * scale;
            var limit = Math.Min(1.0, MaxSide / Math.Max(width, height));
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = Math.Max(1, (uint)(width * limit)),
                DestinationHeight = Math.Max(1, (uint)(height * limit)),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            };
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, options);
            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }
            pages.Add(bytes);
            progress?.Report(((int)i + 1, (int)document.PageCount));
        }
        return pages;
    }
}
