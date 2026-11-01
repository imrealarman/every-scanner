using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SScanner.Services;

namespace SScanner.Models;

public enum EnhanceMode { Original, Magic, Gray, BlackWhite, Text }

/// <summary>One scanned page: the cropped source image plus its current enhanced rendering.</summary>
public sealed class ScanPage : INotifyPropertyChanged
{
    private static readonly SolidColorBrush AccentBrush = new(ColorHelper.FromArgb(255, 0x26, 0x80, 0xEB));
    private static readonly SolidColorBrush IdleBrush = new(Colors.Transparent); // tile fill already sets it apart in both themes

    private int number;
    private bool isSelected = true;
    private bool isCurrent;
    private int renderVersion;

    public ScanPage(byte[] raw, EnhanceMode mode = EnhanceMode.Original)
    {
        Original = raw;
        Raw = raw;
        Mode = mode;
    }

    /// <summary>The page as first prepared, kept so edits can be reset.</summary>
    public byte[] Original { get; }

    /// <summary>Current geometry (after crop and rotation) as PNG; enhancement is always re-rendered from this.</summary>
    public byte[] Raw { get; private set; }

    /// <summary>Enhanced page (JPEG, or PNG for black-and-white) used for display and PDF export.</summary>
    public byte[] Processed { get; private set; } = [];

    public BitmapImage? Preview { get; private set; }

    /// <summary>Size of the rendered page in pixels (shown by the crop tool).</summary>
    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    public EnhanceMode Mode { get; private set; }

    /// <summary>-100..100</summary>
    public int Brightness { get; private set; }

    /// <summary>-100..100</summary>
    public int Contrast { get; private set; }

    /// <summary>-100..100. Light tones: raise to whiten a pale pattern behind text.</summary>
    public int Highlights { get; private set; }

    /// <summary>-100..100. Dark tones: lower to make text blacker.</summary>
    public int Shadows { get; private set; }

    /// <summary>Fine straightening in degrees (positive = counter-clockwise). Applied when rendering; baked into <see cref="Raw"/> before crop or 90° rotation.</summary>
    public double Angle { get; private set; }

    public int Number
    {
        get => number;
        set { number = value; Notify(nameof(Number), nameof(Label)); }
    }

    public string Label => $"{Number:00}";

    public bool IsSelected
    {
        get => isSelected;
        set { if (isSelected != value) { isSelected = value; Notify(nameof(IsSelected)); SelectionChanged?.Invoke(); } }
    }

    public bool IsCurrent
    {
        get => isCurrent;
        set { isCurrent = value; Notify(nameof(IsCurrent), nameof(CardBorderBrush), nameof(CardBorderThickness)); }
    }

    public Brush CardBorderBrush => IsCurrent ? AccentBrush : IdleBrush;

    public Thickness CardBorderThickness => new(IsCurrent ? 2 : 1);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the save-selection checkbox changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised after the page has been re-rendered (new look, crop, rotation…).</summary>
    public event Action<ScanPage>? Rendered;

    private (byte[] Source, int Quality, Task<byte[]> Task)? pdfCache;

    /// <summary>
    /// The page as it goes into a PDF (JPEG at <paramref name="quality"/>). Built once in the background and
    /// reused until the page or the quality changes, so saving doesn't have to re-encode every page.
    /// </summary>
    public Task<byte[]> PdfBytes(int quality)
    {
        var source = Processed;
        if (pdfCache is { } cache && ReferenceEquals(cache.Source, source) && cache.Quality == quality) return cache.Task;
        var task = Task.Run(() => OutputService.ForPdf(source, quality));
        pdfCache = (source, quality, task);
        return task;
    }

    private (byte[] Source, Task<OcrService.Page?> Task)? ocrCache;

    /// <summary>The text found on the page as it is now. Read once and reused until the page changes.</summary>
    public Task<OcrService.Page?> Text()
    {
        var source = Processed;
        if (ocrCache is { } cache && ReferenceEquals(cache.Source, source)) return cache.Task;
        var task = OcrService.RecognizeAsync(source);
        ocrCache = (source, task);
        return task;
    }

    public async Task SetModeAsync(EnhanceMode mode)
    {
        Mode = mode;
        await RenderAsync();
    }

    /// <summary>Sets brightness and contrast without rendering; call <see cref="RenderAsync"/> afterwards.</summary>
    public void SetAdjustments(int brightness, int contrast, int highlights = 0, int shadows = 0)
    {
        Brightness = brightness;
        Contrast = contrast;
        Highlights = highlights;
        Shadows = shadows;
    }

    public async Task SetLookAsync(EnhanceMode mode, int brightness, int contrast, int highlights, int shadows)
    {
        Mode = mode;
        SetAdjustments(brightness, contrast, highlights, shadows);
        await RenderAsync();
    }

    /// <summary>Sets the fine straightening angle without rendering; call <see cref="RenderAsync"/> afterwards.</summary>
    public void SetAngle(double degrees) => Angle = degrees;

    public async Task RotateAsync(bool clockwise)
    {
        var (raw, angle) = TakeAngle();
        Raw = await Task.Run(() => ImageEnhancer.Rotate(Bake(raw, angle), clockwise));
        await RenderAsync();
    }

    /// <summary>Crops to a rectangle given as fractions (0..1) of the current (straightened) page.</summary>
    public async Task CropAsync(double x, double y, double width, double height)
    {
        var (raw, angle) = TakeAngle();
        Raw = await Task.Run(() => ImageEnhancer.Crop(Bake(raw, angle), x, y, width, height));
        await RenderAsync();
    }

    private readonly SemaphoreSlim brushGate = new(1, 1);

    /// <summary>Everything an undo needs to put this page back: the image (immutable, so only a reference) and the look.</summary>
    public readonly record struct State(byte[] Raw, EnhanceMode Mode, int Brightness, int Contrast, int Highlights, int Shadows, double Angle);

    public State Save() => new(Raw, Mode, Brightness, Contrast, Highlights, Shadows, Angle);

    /// <summary>Puts a saved state back without rendering; returns false when nothing differs. Call <see cref="RenderAsync"/> when true.</summary>
    public bool Restore(State state)
    {
        if (state.Equals(Save())) return false;
        Raw = state.Raw;
        Mode = state.Mode;
        SetAdjustments(state.Brightness, state.Contrast, state.Highlights, state.Shadows);
        Angle = state.Angle;
        return true;
    }

    /// <summary>
    /// Paints one dodge/burn stroke into the page. Strokes queue up, so a fast painter never loses one while the
    /// previous one is still rendering. The fine straightening angle is baked in first so the stroke lands where it was drawn.
    /// </summary>
    public async Task DodgeBurnAsync(ImageEnhancer.BrushStroke stroke)
    {
        await brushGate.WaitAsync();
        try
        {
            var baked = Bake(Raw, Angle);
            Angle = 0;
            Raw = await Task.Run(() => ImageEnhancer.DodgeBurn(baked, stroke));
            await RenderAsync();
        }
        finally { brushGate.Release(); }
    }

    /// <summary>Puts a new picture on the page: <paramref name="place"/> gets the page image (straightening baked in) and returns the result.</summary>
    public async Task PlaceAsync(Func<byte[], byte[]> place)
    {
        await brushGate.WaitAsync();
        try
        {
            var baked = Bake(Raw, Angle);
            Angle = 0;
            Raw = await Task.Run(() => place(baked));
            await RenderAsync();
        }
        finally { brushGate.Release(); }
    }

    /// <summary>The page image with any fine straightening baked in (what the preview shows), for tools that cut it up.</summary>
    public Task<byte[]> BakedAsync()
    {
        var (raw, angle) = (Raw, Angle);
        return Task.Run(() => Bake(raw, angle));
    }

    /// <summary>Flattens the page to four corners given as fractions (0..1): top-left, top-right, bottom-right, bottom-left.</summary>
    public async Task FlattenToCornersAsync(IReadOnlyList<(double X, double Y)> corners)
    {
        var (raw, angle) = TakeAngle();
        Raw = await Task.Run(() => ImageEnhancer.FlattenToCorners(Bake(raw, angle), corners));
        await RenderAsync();
    }

    /// <summary>Finds the page outline on the page as it is now (fine straightening included), or null if none is clear.</summary>
    public Task<(double X, double Y)[]?> DetectCornersAsync()
    {
        var (raw, angle) = (Raw, Angle);
        return Task.Run(() => ImageEnhancer.DetectCorners(Bake(raw, angle)));
    }

    /// <summary>Detects and fixes tilt and crooked page edges on this page.</summary>
    public async Task AutoStraightenAsync()
    {
        var (raw, angle) = TakeAngle();
        Raw = await Task.Run(() => ImageEnhancer.AutoStraighten(Bake(raw, angle)));
        await RenderAsync();
    }

    /// <summary>Undoes crop, rotation, straightening, brightness, contrast, highlights and shadows.</summary>
    public async Task ResetEditsAsync()
    {
        Raw = Original;
        Angle = 0;
        SetAdjustments(0, 0);
        await RenderAsync();
    }

    /// <summary>Hands out the current image and angle, and clears the angle because the caller is about to bake it in.</summary>
    private (byte[] Raw, double Angle) TakeAngle()
    {
        var taken = (Raw, Angle);
        Angle = 0;
        return taken;
    }

    private static byte[] Bake(byte[] raw, double angle) =>
        Math.Abs(angle) > 0.01 ? ImageEnhancer.RotateBy(raw, angle) : raw;

    /// <summary>Re-renders the page; if a newer render starts meanwhile, this one is discarded.</summary>
    public async Task RenderAsync()
    {
        var version = ++renderVersion;
        var (raw, mode, brightness, contrast, angle, highlights, shadows) = (Raw, Mode, Brightness, Contrast, Angle, Highlights, Shadows);
        var rendered = await Task.Run(() => ImageEnhancer.RenderPage(raw, mode, brightness, contrast, angle, highlights, shadows));
        if (version != renderVersion) return;
        var preview = await ImageHelper.ToBitmapAsync(rendered.Bytes);
        if (version != renderVersion) return;

        Processed = rendered.Bytes;
        PixelWidth = rendered.Width;
        PixelHeight = rendered.Height;
        Preview = preview;
        Notify(nameof(Preview), nameof(Mode));
        Rendered?.Invoke(this);
    }

    private void Notify(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
