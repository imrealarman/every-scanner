using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace SScanner.Services;

public static class ImageHelper
{
    /// <summary>Decodes encoded image bytes into a BitmapImage. Must be called on the UI thread.</summary>
    public static async Task<BitmapImage> ToBitmapAsync(byte[] encoded, int decodeWidth = 1100)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(encoded.AsBuffer());
        stream.Seek(0);
        var bitmap = new BitmapImage { DecodePixelWidth = decodeWidth };
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
