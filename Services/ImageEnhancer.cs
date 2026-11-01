using OpenCvSharp;
using SScanner.Models;

namespace SScanner.Services;

/// <summary>Offline document clean-up built on OpenCV: crop, deskew, flatten lighting, binarize.</summary>
public static class ImageEnhancer
{
    /// <summary>
    /// Decodes a raw scan, crops it to the page, straightens it and returns it as PNG.
    /// Returns null when <paramref name="skipBlank"/> is set and the page has no content.
    /// </summary>
    public static byte[]? Prepare(byte[] encoded, bool skipBlank, bool fast = false, bool straighten = true)
    {
        // Fast scan: take the page as delivered, no crop/deskew/blank check.
        if (fast) return encoded;
        // Nothing to change: keep the scanner's own bytes instead of decoding and re-encoding them.
        if (!straighten && !skipBlank) return encoded;

        using var src = Cv2.ImDecode(encoded, ImreadModes.Color);
        if (src.Empty()) throw new InvalidDataException("The image could not be decoded.");
        if (!straighten) return skipBlank && IsBlank(src) ? null : encoded;

        using var cropped = TryCrop(src);
        using var upright = Deskew(cropped ?? src);
        if (skipBlank && IsBlank(upright)) return null;
        return EncodePng(upright);
    }

    /// <summary>Internal copies are re-read many times, so favour speed over file size (level 1 is several times faster than the default).</summary>
    private static byte[] EncodePng(Mat mat) =>
        mat.ImEncode(".png", new ImageEncodingParam(ImwriteFlags.PngCompression, 1));

    /// <summary>A paper size in millimetres, portrait.</summary>
    public readonly record struct PaperSize(string Name, double WidthMm, double HeightMm);

    public static readonly PaperSize[] PaperSizes =
    [
        new("A0", 841, 1189), new("A1", 594, 841), new("A2", 420, 594), new("A3", 297, 420), new("A4", 210, 297),
        new("A5", 148, 210), new("A6", 105, 148), new("B4", 250, 353), new("B5", 176, 250),
        new("Letter", 215.9, 279.4), new("Legal", 215.9, 355.6), new("Tabloid", 279.4, 431.8),
    ];

    /// <summary>Pixels per millimetre for blank pages: 200 dpi, lowered for very large sheets so the long side stays under 4800 px.</summary>
    private static double PixelsPerMm(PaperSize paper) => Math.Min(200 / 25.4, 4800 / Math.Max(paper.WidthMm, paper.HeightMm));

    public static (int Width, int Height) PaperPixels(PaperSize paper, bool landscape)
    {
        var k = PixelsPerMm(paper);
        var (w, h) = ((int)Math.Round(paper.WidthMm * k), (int)Math.Round(paper.HeightMm * k));
        return landscape ? (h, w) : (w, h);
    }

    /// <summary>An empty white page of the given paper size as PNG.</summary>
    public static byte[] BlankPage(PaperSize paper, bool landscape)
    {
        var (w, h) = PaperPixels(paper, landscape);
        using var page = new Mat(h, w, MatType.CV_8UC3, Scalar.All(255));
        return EncodePng(page);
    }

    /// <summary>Pixel size of an encoded image.</summary>
    public static (int Width, int Height) SizeOf(byte[] encoded)
    {
        using var mat = Cv2.ImDecode(encoded, ImreadModes.Color);
        if (mat.Empty()) throw new InvalidDataException("The image could not be decoded.");
        return (mat.Width, mat.Height);
    }

    /// <summary>
    /// Puts an image onto a page. The image is given the chosen look, scaled to <paramref name="width"/> x <paramref name="height"/>
    /// (fractions of the page width and height), turned by <paramref name="angle"/> degrees (positive = counter-clockwise) and
    /// centred at (<paramref name="centerX"/>, <paramref name="centerY"/>), also fractions. <paramref name="scale"/> below 1 gives a
    /// smaller result for live previews.
    /// </summary>
    public static byte[] PlaceImage(byte[] page, byte[] image, EnhanceMode mode, int brightness, int contrast,
        double centerX, double centerY, double width, double height, double angle, double scale = 1)
    {
        using var decodedPage = Cv2.ImDecode(page, ImreadModes.Color);
        using var decodedImage = Cv2.ImDecode(image, ImreadModes.Color);
        if (decodedPage.Empty() || decodedImage.Empty()) throw new InvalidDataException("The image could not be decoded.");

        using var target = scale < 1 ? decodedPage.Resize(new Size(Math.Max(1, (int)(decodedPage.Width * scale)), Math.Max(1, (int)(decodedPage.Height * scale))), 0, 0, InterpolationFlags.Area) : decodedPage.Clone();
        var (w, h) = (Math.Max(2, (int)Math.Round(width * target.Width)), Math.Max(2, (int)Math.Round(height * target.Height)));

        using var looked = RenderMat(decodedImage, mode, brightness, contrast, 0, 0);
        using var colour = looked.Channels() == 1 ? looked.CvtColor(ColorConversionCodes.GRAY2BGR) : looked.Clone();
        using var sized = colour.Resize(new Size(w, h), 0, 0, w < colour.Width ? InterpolationFlags.Area : InterpolationFlags.Cubic);

        // Turn about the image's own centre, then move that centre to the chosen spot on the page.
        var theta = angle * Math.PI / 180;
        var (c, s) = (Math.Cos(theta), Math.Sin(theta));
        var tx = centerX * target.Width - (c * w / 2 + s * h / 2);
        var ty = centerY * target.Height - (-s * w / 2 + c * h / 2);
        using var matrix = Mat.FromArray(new[,] { { c, s, tx }, { -s, c, ty } });

        using var placed = new Mat();
        using var mask = new Mat(sized.Size(), MatType.CV_8UC1, Scalar.All(255));
        using var placedMask = new Mat();
        Cv2.WarpAffine(sized, placed, matrix, target.Size(), InterpolationFlags.Cubic, BorderTypes.Constant, Scalar.All(0));
        Cv2.WarpAffine(mask, placedMask, matrix, target.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
        Cv2.Threshold(placedMask, placedMask, 127, 255, ThresholdTypes.Binary);
        placed.CopyTo(target, placedMask);
        return EncodePng(target);
    }

    /// <summary>Fixes a crooked or tilted page: flattens the page outline if one is found, then removes remaining tilt.</summary>
    public static byte[] AutoStraighten(byte[] raw)
    {
        using var src = Cv2.ImDecode(raw, ImreadModes.Color);
        using var cropped = TryCrop(src);
        using var upright = Deskew(cropped ?? src);
        return EncodePng(upright);
    }

    /// <summary>Rotates by <paramref name="angle"/> degrees (positive = counter-clockwise), filling corners with white.</summary>
    public static byte[] RotateBy(byte[] raw, double angle)
    {
        using var src = Cv2.ImDecode(raw, ImreadModes.Color);
        using var rotated = RotateFine(src, angle);
        return EncodePng(rotated);
    }

    /// <summary>
    /// Renders a page with the chosen look. Brightness and contrast (-100..100) are applied after
    /// enhancement; in black-and-white mode brightness moves the threshold and contrast is ignored.
    /// </summary>
    public static byte[] Render(byte[] raw, EnhanceMode mode, int brightness = 0, int contrast = 0, double angle = 0, int highlights = 0, int shadows = 0) =>
        RenderPage(raw, mode, brightness, contrast, angle, highlights, shadows).Bytes;

    /// <summary>A rendered page and its size in pixels.</summary>
    public readonly record struct RenderedPage(byte[] Bytes, int Width, int Height);

    /// <summary>Same as <see cref="Render"/>, but also reports the page's pixel size (used by the crop tool).</summary>
    public static RenderedPage RenderPage(byte[] raw, EnhanceMode mode, int brightness = 0, int contrast = 0, double angle = 0, int highlights = 0, int shadows = 0)
    {
        using var decoded = Cv2.ImDecode(raw, ImreadModes.Color);
        using var src = Math.Abs(angle) > 0.01 ? RotateFine(decoded, angle) : decoded.Clone();
        using var result = RenderMat(src, mode, brightness, contrast, highlights, shadows);
        var bytes = mode == EnhanceMode.BlackWhite
            ? EncodePng(result)
            : result.ImEncode(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
        return new RenderedPage(bytes, result.Width, result.Height);
    }

    private static Mat RenderMat(Mat src, EnhanceMode mode, int brightness, int contrast, int highlights, int shadows) => mode switch
    {
        EnhanceMode.Magic => Adjusted(MagicColor(src), brightness, contrast, highlights, shadows),
        EnhanceMode.Gray => Adjusted(Grayscale(src), brightness, contrast, highlights, shadows),
        EnhanceMode.Text => Adjusted(TextLook(src), brightness, contrast, highlights, shadows),
        EnhanceMode.BlackWhite => BlackWhite(src, brightness, highlights, shadows),
        _ => Adjusted(src.Clone(), brightness, contrast, highlights, shadows),
    };

    /// <summary>A look chosen by <see cref="FindSmartLook"/>.</summary>
    public readonly record struct SmartLook(EnhanceMode Mode, int Brightness, int Contrast, int Highlights, int Shadows);

    /// <summary>
    /// Smart mode: tries a handful of looks on a small copy of the page, scores how readable each one is, and returns
    /// the best. Returns null for a page with no text-like content (leave those alone).
    /// </summary>
    public static SmartLook? FindSmartLook(byte[] raw)
    {
        using var decoded = Cv2.ImDecode(raw, ImreadModes.Color);
        if (decoded.Empty()) return null;
        var scale = Math.Min(1.0, 700.0 / Math.Max(decoded.Width, decoded.Height));
        using var small = new Mat();
        Cv2.Resize(decoded, small, new Size(0, 0), scale, scale, InterpolationFlags.Area);

        using var baseline = RenderMat(small, EnhanceMode.Gray, 0, 0, 0, 0);
        var baselineInk = DarkFraction(baseline);
        if (baselineInk < 0.002) return null; // blank or photo-only page: nothing to read

        SmartLook best = new(EnhanceMode.Text, 0, 0, 0, 0);
        var bestScore = double.MinValue;
        foreach (var mode in new[] { EnhanceMode.Text, EnhanceMode.Gray })
            foreach (var highlights in new[] { 0, 40, 70, 100 })
                foreach (var shadows in new[] { 0, -30, -60 })
                {
                    var look = new SmartLook(mode, 0, 0, highlights, shadows);
                    var score = Readability(small, look, baselineInk);
                    if (score > bestScore) { best = look; bestScore = score; }
                }

        // Final touch: lift the paper to white.
        using var chosen = RenderMat(small, best.Mode, 0, 0, best.Highlights, best.Shadows);
        var paper = PaperLevel(chosen);
        var brightness = (int)Math.Clamp((242 - paper) / 1.27, 0, 40);
        return best with { Brightness = brightness };
    }

    /// <summary>Higher = easier to read: strong ink/paper separation and a clean (low-noise) paper, without losing ink.</summary>
    private static double Readability(Mat small, SmartLook look, double baselineInk)
    {
        using var rendered = RenderMat(small, look.Mode, look.Brightness, look.Contrast, look.Highlights, look.Shadows);
        if (DarkFraction(rendered) < baselineInk * 0.8) return double.MinValue; // would erase real text

        using var gray = rendered.Channels() == 1 ? rendered.Clone() : rendered.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var paperMask = new Mat();
        Cv2.Threshold(gray, paperMask, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        using var inkMask = new Mat();
        Cv2.BitwiseNot(paperMask, inkMask);
        if (Cv2.CountNonZero(paperMask) == 0 || Cv2.CountNonZero(inkMask) == 0) return double.MinValue;

        Cv2.MeanStdDev(gray, out var paperMean, out var paperStd, paperMask);
        Cv2.MeanStdDev(gray, out var inkMean, out _, inkMask);
        var separation = (paperMean.Val0 - inkMean.Val0) / 255;
        var noise = paperStd.Val0 / 255;
        var change = look.Highlights / 100.0 * 0.02 + Math.Abs(look.Shadows) / 100.0 * 0.02 + (look.Mode == EnhanceMode.Gray ? 0.01 : 0);
        return separation - 1.5 * noise - change;
    }

    private static double DarkFraction(Mat image)
    {
        using var gray = image.Channels() == 1 ? image.Clone() : image.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var dark = new Mat();
        Cv2.Threshold(gray, dark, 128, 255, ThresholdTypes.BinaryInv);
        return Cv2.CountNonZero(dark) / (double)(gray.Width * gray.Height);
    }

    /// <summary>Typical paper brightness: the median of the light half of the page.</summary>
    private static double PaperLevel(Mat image)
    {
        using var gray = image.Channels() == 1 ? image.Clone() : image.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var paperMask = new Mat();
        Cv2.Threshold(gray, paperMask, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        Cv2.MeanStdDev(gray, out var mean, out _, paperMask);
        return mean.Val0;
    }

    public static byte[] Rotate(byte[] raw, bool clockwise)
    {
        using var src = Cv2.ImDecode(raw, ImreadModes.Color);
        using var dst = new Mat();
        Cv2.Rotate(src, dst, clockwise ? RotateFlags.Rotate90Clockwise : RotateFlags.Rotate90Counterclockwise);
        return EncodePng(dst);
    }

    private static Mat MagicColor(Mat src)
    {
        var channels = Cv2.Split(src);
        try
        {
            for (var i = 0; i < channels.Length; i++)
            {
                var flat = Flatten(channels[i]);
                channels[i].Dispose();
                channels[i] = flat;
            }
            using var merged = new Mat();
            Cv2.Merge(channels, merged);
            return Sharpen(merged);
        }
        finally
        {
            foreach (var c in channels) c.Dispose();
        }
    }

    private static Mat Grayscale(Mat src)
    {
        using var gray = src.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var flat = Flatten(gray);
        return Sharpen(flat);
    }

    /// <summary>
    /// Print-friendly look for Persian and English text: whitens the paper, darkens the ink and sharpens edges,
    /// but stays greyscale (not hard black/white) so thin strokes, dots and diacritics are not lost.
    /// </summary>
    private static Mat TextLook(Mat src)
    {
        using var gray = src.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var flat = Flatten(gray);

        const int Black = 70, White = 205;
        using var lut = new Mat(1, 256, MatType.CV_8UC1);
        for (var v = 0; v < 256; v++)
        {
            var t = Math.Clamp((v - Black) / (double)(White - Black), 0, 1);
            lut.Set(0, v, (byte)Math.Round(Math.Pow(t, 1.25) * 255));
        }
        using var leveled = new Mat();
        Cv2.LUT(flat, lut, leveled);
        return Sharpen(leveled);
    }

    private static Mat BlackWhite(Mat src, int brightness, int highlights, int shadows)
    {
        using var gray = src.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var flatBase = Flatten(gray);
        using var flat = ToneCurve(flatBase, highlights, shadows);
        var bw = new Mat();
        var otsu = Cv2.Threshold(flat, bw, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        if (brightness != 0)
        {
            // Brighter = lower threshold = more white.
            var threshold = Math.Clamp(otsu - brightness * 0.6, 1, 254);
            Cv2.Threshold(flat, bw, threshold, 255, ThresholdTypes.Binary);
        }
        return bw;
    }

    /// <summary>
    /// Highlights and shadows (-100..100) as a tone curve. Highlights act on the light tones: raise them to wash out a
    /// pale pattern behind text, lower them to recover washed-out detail. Shadows act on the dark tones: lower them to
    /// make ink blacker, raise them to open up dark areas. Always returns a new Mat.
    /// </summary>
    private static Mat ToneCurve(Mat mat, int highlights, int shadows)
    {
        if (highlights == 0 && shadows == 0) return mat.Clone();
        double h = highlights / 100.0, s = shadows / 100.0;
        using var lut = new Mat(1, 256, MatType.CV_8UC1);
        for (var v = 0; v < 256; v++)
        {
            var x = v / 255.0;
            var wh = Smooth((x - 0.25) / 0.55);
            var ws = 1 - Smooth((x - 0.1) / 0.55);
            x += h * wh * (h > 0 ? 1 - x : x);
            x += s * ws * (s > 0 ? 1 - x : x);
            lut.Set(0, v, (byte)Math.Round(Math.Clamp(x, 0, 1) * 255));
        }
        var result = new Mat();
        Cv2.LUT(mat, lut, result);
        return result;
    }

    private static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Highlights/shadows, then linear contrast about mid-grey plus brightness offset. Takes ownership of <paramref name="mat"/>.</summary>
    private static Mat Adjusted(Mat mat, int brightness, int contrast, int highlights = 0, int shadows = 0)
    {
        if (highlights != 0 || shadows != 0)
        {
            var toned = ToneCurve(mat, highlights, shadows);
            mat.Dispose();
            mat = toned;
        }
        if (brightness == 0 && contrast == 0) return mat;
        var alpha = contrast >= 0 ? 1 + contrast / 50.0 : 1 + contrast / 125.0;
        var beta = 128 - 128 * alpha + brightness * 1.27;
        var result = new Mat();
        mat.ConvertTo(result, mat.Type(), alpha, beta);
        mat.Dispose();
        return result;
    }

    /// <summary>
    /// Cuts a page in two, e.g. a book scanned open. <paramref name="sideBySide"/> cuts into a left and a right half,
    /// otherwise into a top and a bottom half; <paramref name="position"/> (0..1) is where the cut falls.
    /// </summary>
    public static (byte[] First, byte[] Second) Split(byte[] raw, bool sideBySide, double position)
    {
        using var src = Cv2.ImDecode(raw, ImreadModes.Color);
        var length = sideBySide ? src.Width : src.Height;
        var cut = Math.Clamp((int)Math.Round(position * length), 8, Math.Max(8, length - 8));
        var first = sideBySide ? new Rect(0, 0, cut, src.Height) : new Rect(0, 0, src.Width, cut);
        var second = sideBySide ? new Rect(cut, 0, src.Width - cut, src.Height) : new Rect(0, cut, src.Width, src.Height - cut);
        using var a = new Mat(src, first);
        using var b = new Mat(src, second);
        return (EncodePng(a), EncodePng(b));
    }

    /// <summary>Crops to a rectangle given as fractions (0..1) of the page width and height.</summary>
    public static byte[] Crop(byte[] raw, double x, double y, double width, double height)
    {
        using var src = Cv2.ImDecode(raw, ImreadModes.Color);
        var left = Math.Clamp((int)Math.Round(x * src.Width), 0, src.Width - 1);
        var top = Math.Clamp((int)Math.Round(y * src.Height), 0, src.Height - 1);
        var w = Math.Clamp((int)Math.Round(width * src.Width), 1, src.Width - left);
        var h = Math.Clamp((int)Math.Round(height * src.Height), 1, src.Height - top);
        if (w < 16 || h < 16) return raw;
        using var region = new Mat(src, new Rect(left, top, w, h));
        return EncodePng(region);
    }

    /// <summary>Which tones a dodge/burn stroke affects.</summary>
    public enum ToneRange { Shadows, Midtones, Highlights }

    /// <summary>What a brush stroke does: lighten, darken, or paint over marks with the paper around them.</summary>
    public enum BrushKind { Dodge, Burn, Erase }

    /// <summary>
    /// One brush stroke. Points are fractions (0..1) of the page, <paramref name="Diameter"/> is a fraction of the page
    /// width and <paramref name="Exposure"/> is 1..100 (how strongly the stroke lightens, darkens or erases).
    /// </summary>
    public sealed record BrushStroke(IReadOnlyList<(double X, double Y)> Points, double Diameter, BrushKind Kind, ToneRange Range, int Exposure);

    /// <summary>
    /// Lightens (dodge) or darkens (burn) the area under a brush stroke, weighted towards the chosen tonal range, with a
    /// soft edge; or erases it, replacing marks smaller than the brush with the paper around them. A stroke never builds
    /// up on itself where it overlaps, so its strength is even along its length.
    /// </summary>
    public static byte[] DodgeBurn(byte[] raw, BrushStroke stroke)
    {
        if (stroke.Points.Count == 0) return raw;
        using var src = Cv2.ImDecode(raw, ImreadModes.Color);
        var radius = Math.Max(stroke.Diameter * src.Width / 2, 1);
        var points = stroke.Points
            .Select(p => new Point((int)Math.Round(p.X * src.Width), (int)Math.Round(p.Y * src.Height)))
            .ToArray();

        // Only the area around the stroke is touched (the soft edge reaches a little past the brush).
        var pad = (int)Math.Ceiling(radius * 1.5) + 2;
        var x0 = Math.Clamp(points.Min(p => p.X) - pad, 0, src.Width - 1);
        var y0 = Math.Clamp(points.Min(p => p.Y) - pad, 0, src.Height - 1);
        var x1 = Math.Clamp(points.Max(p => p.X) + pad, x0 + 1, src.Width);
        var y1 = Math.Clamp(points.Max(p => p.Y) + pad, y0 + 1, src.Height);
        var area = new Rect(x0, y0, x1 - x0, y1 - y0);
        using var region = new Mat(src, area);

        using var mask = new Mat(area.Size, MatType.CV_8UC1, Scalar.All(0));
        var thickness = Math.Max(1, (int)Math.Round(radius * 2));
        var local = points.Select(p => new Point(p.X - x0, p.Y - y0)).ToArray();
        if (local.Length == 1) Cv2.Circle(mask, local[0], (int)Math.Round(radius), Scalar.All(255), -1, LineTypes.AntiAlias);
        else for (var i = 1; i < local.Length; i++) Cv2.Line(mask, local[i - 1], local[i], Scalar.All(255), thickness, LineTypes.AntiAlias);
        if (radius > 2) Cv2.GaussianBlur(mask, mask, new Size(0, 0), radius * 0.25);

        // Strength per pixel = brush coverage x exposure (x how much that pixel's tone is in the chosen range, for dodge/burn).
        using var coverage = new Mat();
        mask.ConvertTo(coverage, MatType.CV_32FC1, 1 / 255.0);
        var exposure = Math.Clamp(stroke.Exposure, 1, 100) / 100.0;
        using var amount = new Mat();
        if (stroke.Kind == BrushKind.Erase)
            coverage.ConvertTo(amount, MatType.CV_32FC1, exposure);
        else
        {
            using var gray = region.CvtColor(ColorConversionCodes.BGR2GRAY);
            using var rangeLut = new Mat(1, 256, MatType.CV_8UC1);
            for (var v = 0; v < 256; v++)
                rangeLut.Set(0, v, (byte)Math.Round(RangeWeight(v / 255.0, stroke.Range) * 255));
            using var weight = new Mat();
            Cv2.LUT(gray, rangeLut, weight);
            using var tone = new Mat();
            weight.ConvertTo(tone, MatType.CV_32FC1, exposure / 255.0);
            Cv2.Multiply(coverage, tone, amount);
        }
        using var amount3 = new Mat();
        Cv2.Merge([amount, amount, amount], amount3);

        using var pixels = new Mat();
        region.ConvertTo(pixels, MatType.CV_32FC3, 1 / 255.0);
        using var result = new Mat();
        if (stroke.Kind == BrushKind.Erase)
        {
            // pixels + (paper - pixels) * amount
            using var paper8 = PaperAround(region, radius);
            using var paper = new Mat();
            paper8.ConvertTo(paper, MatType.CV_32FC3, 1 / 255.0);
            using var gap = new Mat();
            Cv2.Subtract(paper, pixels, gap);
            Cv2.Multiply(gap, amount3, gap);
            Cv2.Add(pixels, gap, result);
        }
        else if (stroke.Kind == BrushKind.Burn)
        {
            // pixels * (1 - amount)
            using var keep = new Mat();
            Cv2.Subtract(Scalar.All(1), amount3, keep);
            Cv2.Multiply(pixels, keep, result);
        }
        else
        {
            // pixels + (1 - pixels) * amount
            using var headroom = new Mat();
            Cv2.Subtract(Scalar.All(1), pixels, headroom);
            Cv2.Multiply(headroom, amount3, headroom);
            Cv2.Add(pixels, headroom, result);
        }
        using var back = new Mat();
        result.ConvertTo(back, MatType.CV_8UC3, 255);
        back.CopyTo(region);
        return EncodePng(src);
    }

    /// <summary>
    /// Estimates the paper under and around a brush stroke: the lightest colour within a brush radius in each channel
    /// (which drops any mark thinner than the brush), smoothed. Works on a quarter-size copy for big brushes.
    /// </summary>
    private static Mat PaperAround(Mat region, double radius)
    {
        var scale = radius > 30 ? 0.25 : 1.0;
        using var small = new Mat();
        if (scale < 1) Cv2.Resize(region, small, new Size(0, 0), scale, scale, InterpolationFlags.Area);
        else region.CopyTo(small);

        var diameter = Math.Max(3, (int)Math.Round(radius * 2 * scale)) | 1;
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(diameter, diameter));
        using var lightest = new Mat();
        Cv2.Dilate(small, lightest, kernel);
        using var smooth = new Mat();
        Cv2.GaussianBlur(lightest, smooth, new Size(0, 0), Math.Max(1, diameter / 4.0));

        var paper = new Mat();
        Cv2.Resize(smooth, paper, region.Size(), 0, 0, InterpolationFlags.Linear);
        return paper;
    }

    /// <summary>How strongly a tone (0 = black, 1 = white) belongs to the range: 1 inside it, easing to 0 outside.</summary>
    private static double RangeWeight(double tone, ToneRange range) => range switch
    {
        ToneRange.Shadows => 1 - Smooth((tone - 0.15) / 0.45),
        ToneRange.Highlights => Smooth((tone - 0.55) / 0.45),
        _ => 1 - Smooth(Math.Abs(tone - 0.5) / 0.4),
    };

    /// <summary>
    /// Divides a channel by an estimate of its background (paper colour + lighting), which whitens
    /// the paper and removes shadows while keeping ink. The estimate runs on a quarter-size copy.
    /// </summary>
    private static Mat Flatten(Mat channel)
    {
        var small = new Size(Math.Max(channel.Width / 4, 1), Math.Max(channel.Height / 4, 1));
        using var reduced = new Mat();
        Cv2.Resize(channel, reduced, small, 0, 0, InterpolationFlags.Area);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var dilated = new Mat();
        Cv2.Dilate(reduced, dilated, kernel);

        using var smooth = new Mat();
        Cv2.MedianBlur(dilated, smooth, 21);

        using var background = new Mat();
        Cv2.Resize(smooth, background, channel.Size(), 0, 0, InterpolationFlags.Linear);

        var result = new Mat();
        Cv2.Divide(channel, background, result, 255);
        return result;
    }

    private static Mat Sharpen(Mat src)
    {
        using var blur = new Mat();
        Cv2.GaussianBlur(src, blur, new Size(0, 0), 1.2);
        var result = new Mat();
        Cv2.AddWeighted(src, 1.4, blur, -0.4, 0, result);
        return result;
    }

    private static bool IsBlank(Mat bgr)
    {
        using var gray = bgr.CvtColor(ColorConversionCodes.BGR2GRAY);
        var margin = new Rect(gray.Width / 20, gray.Height / 20, gray.Width * 9 / 10, gray.Height * 9 / 10);
        using var inner = new Mat(gray, margin);
        using var flat = Flatten(inner);
        using var ink = new Mat();
        Cv2.Threshold(flat, ink, 170, 255, ThresholdTypes.BinaryInv);
        return Cv2.CountNonZero(ink) / (double)(ink.Width * ink.Height) < 0.0008;
    }

    /// <summary>Finds the largest four-sided outline (a photographed or fed page) and flattens it.</summary>
    private static Mat? TryCrop(Mat src)
    {
        var corners = FindPageCorners(src);
        return corners is null ? null : WarpQuad(src, corners);
    }

    /// <summary>The page outline as four corners (top-left, top-right, bottom-right, bottom-left) in pixels, or null if none is found.</summary>
    private static Point2f[]? FindPageCorners(Mat src)
    {
        var scale = Math.Min(1.0, 600.0 / Math.Max(src.Width, src.Height));
        using var small = new Mat();
        Cv2.Resize(src, small, new Size(0, 0), scale, scale, InterpolationFlags.Area);
        using var gray = small.CvtColor(ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);

        using var edges = new Mat();
        Cv2.Canny(gray, edges, 50, 150);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.Dilate(edges, edges, kernel);

        Cv2.FindContours(edges, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var total = small.Width * (double)small.Height;

        Point[]? best = null;
        var bestArea = 0.0;
        foreach (var contour in contours)
        {
            var area = Cv2.ContourArea(contour);
            if (area < total * 0.2 || area > total * 0.97 || area <= bestArea) continue;
            var approx = Cv2.ApproxPolyDP(contour, 0.02 * Cv2.ArcLength(contour, true), true);
            if (approx.Length != 4 || !Cv2.IsContourConvex(approx)) continue;
            best = approx;
            bestArea = area;
        }
        if (best is null) return null;

        return OrderCorners(best.Select(p => new Point2f((float)(p.X / scale), (float)(p.Y / scale))).ToArray());
    }

    /// <summary>Stretches the area inside four corners (top-left, top-right, bottom-right, bottom-left) into an upright rectangle.</summary>
    private static Mat? WarpQuad(Mat src, Point2f[] corners)
    {
        var width = (int)Math.Max(Dist(corners[0], corners[1]), Dist(corners[3], corners[2]));
        var height = (int)Math.Max(Dist(corners[0], corners[3]), Dist(corners[1], corners[2]));
        if (width < 100 || height < 100) return null;

        var target = new[] { new Point2f(0, 0), new Point2f(width, 0), new Point2f(width, height), new Point2f(0, height) };
        using var transform = Cv2.GetPerspectiveTransform(corners, target);
        var warped = new Mat();
        Cv2.WarpPerspective(src, warped, transform, new Size(width, height), InterpolationFlags.Cubic);
        return warped;
    }

    /// <summary>
    /// Finds the page outline for the corner tool, as fractions (0..1) of the page: top-left, top-right, bottom-right,
    /// bottom-left. Returns null when no clear page edge is found.
    /// </summary>
    public static (double X, double Y)[]? DetectCorners(byte[] raw)
    {
        using var src = Cv2.ImDecode(raw, ImreadModes.Color);
        var corners = FindPageCorners(src);
        return corners?.Select(c => (Math.Clamp(c.X / (double)src.Width, 0, 1), Math.Clamp(c.Y / (double)src.Height, 0, 1))).ToArray();
    }

    /// <summary>Flattens the page to the four corners given as fractions (0..1): top-left, top-right, bottom-right, bottom-left.</summary>
    public static byte[] FlattenToCorners(byte[] raw, IReadOnlyList<(double X, double Y)> corners)
    {
        using var src = Cv2.ImDecode(raw, ImreadModes.Color);
        var pixels = corners.Select(c => new Point2f((float)(c.X * src.Width), (float)(c.Y * src.Height))).ToArray();
        using var warped = WarpQuad(src, pixels);
        return warped is null ? raw : EncodePng(warped);
    }

    /// <summary>Orders four points as top-left, top-right, bottom-right, bottom-left.</summary>
    private static Point2f[] OrderCorners(Point2f[] p)
    {
        var tl = p.OrderBy(q => q.X + q.Y).First();
        var br = p.OrderBy(q => q.X + q.Y).Last();
        var tr = p.OrderBy(q => q.Y - q.X).First();
        var bl = p.OrderBy(q => q.Y - q.X).Last();
        return [tl, tr, br, bl];
    }

    private static double Dist(Point2f a, Point2f b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>Straightens slightly tilted content; leaves the page alone if the tilt is tiny or implausible.</summary>
    private static Mat Deskew(Mat src)
    {
        using var gray = src.CvtColor(ColorConversionCodes.BGR2GRAY);
        using var ink = new Mat();
        Cv2.Threshold(gray, ink, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        using var points = new Mat();
        Cv2.FindNonZero(ink, points);
        if (points.Rows < 500) return src.Clone();

        var angle = Cv2.MinAreaRect(points).Angle;
        if (angle > 45) angle -= 90;
        if (Math.Abs(angle) < 0.4 || Math.Abs(angle) > 10) return src.Clone();

        return RotateFine(src, angle);
    }

    private static Mat RotateFine(Mat src, double angle)
    {
        using var rotation = Cv2.GetRotationMatrix2D(new Point2f(src.Width / 2f, src.Height / 2f), angle, 1);
        var result = new Mat();
        Cv2.WarpAffine(src, result, rotation, src.Size(), InterpolationFlags.Cubic, BorderTypes.Constant, Scalar.All(255));
        return result;
    }
}
