using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace SScanner;

/// <summary>
/// The app's light and dark colours. The brushes live in App.xaml as shared instances; switching theme
/// just changes their colours, so every element using them updates at once.
/// </summary>
public static class Palette
{
    private static readonly Dictionary<string, (Color Dark, Color Light)> Colours = new()
    {
        ["ChromeBrush"] = (Rgb(0x21, 0x21, 0x21), Rgb(0xFF, 0xFF, 0xFF)),
        ["RailBrush"] = (Rgb(0x25, 0x25, 0x25), Rgb(0xF5, 0xF5, 0xF5)),
        ["PanelBrush"] = (Rgb(0x2B, 0x2B, 0x2B), Rgb(0xF5, 0xF5, 0xF5)),
        ["CanvasBrush"] = (Rgb(0x1B, 0x1B, 0x1B), Rgb(0xE6, 0xE6, 0xE6)),
        ["TileBrush"] = (Rgb(0x34, 0x34, 0x34), Rgb(0xFF, 0xFF, 0xFF)),
        ["BorderBrush1"] = (Rgb(0x3A, 0x3A, 0x3A), Rgb(0xDA, 0xDA, 0xDA)),
        ["BorderBrush2"] = (Rgb(0x4B, 0x4B, 0x4B), Rgb(0xC6, 0xC6, 0xC6)),
        ["TextBrush"] = (Rgb(0xEA, 0xEA, 0xEA), Rgb(0x22, 0x22, 0x22)),
        ["TextMutedBrush"] = (Rgb(0x9B, 0x9B, 0x9B), Rgb(0x6E, 0x6E, 0x6E)),
        ["AccentTintBrush"] = (Rgb(0x26, 0x36, 0x4D), Rgb(0xE3, 0xEE, 0xFC)),
        ["HoverVeilBrush"] = (ColorHelper.FromArgb(0x14, 0xFF, 0xFF, 0xFF), ColorHelper.FromArgb(0x12, 0, 0, 0)),
    };

    public static void Apply(bool light)
    {
        foreach (var (key, pair) in Colours)
            ((SolidColorBrush)Application.Current.Resources[key]).Color = light ? pair.Light : pair.Dark;
    }

    private static Color Rgb(byte r, byte g, byte b) => ColorHelper.FromArgb(255, r, g, b);
}
