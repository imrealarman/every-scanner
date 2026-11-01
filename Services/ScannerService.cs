using System.Runtime.InteropServices;

namespace SScanner.Services;

public enum ScanSource { Auto, Feeder, Flatbed }
public enum ScanColor { Color, Gray, BlackWhite }

public enum PaperSize { Auto, A4, A5, A6, B5, Letter, Legal }

/// <param name="Duplex">Ask the feeder to scan both sides of each sheet, if the scanner can.</param>
/// <param name="Fast">Let the driver hand back JPEG instead of PNG, which is quicker to transfer.</param>
/// <param name="DeviceId">WIA id of the scanner to use; empty picks the only scanner, or asks if there are several.</param>
/// <param name="Paper">Area to scan; Auto leaves it to the scanner.</param>
public sealed record ScanOptions(ScanSource Source, ScanColor Color, int Dpi, bool Duplex = false, bool Fast = false,
    string DeviceId = "", PaperSize Paper = PaperSize.Auto, bool UseTwain = true);

public sealed record ScannerInfo(string Id, string Name);

/// <param name="HardwareDuplex">True if the scanner scanned both sides itself (pages arrive front, back, front, back…).</param>
/// <param name="PaperSizeApplied">False if a page size was requested but the driver refused it (the scanner's own size was used).</param>
public sealed record ScanResult(List<byte[]> Pages, bool HardwareDuplex, bool PaperSizeApplied = true);

/// <summary>Drives any WIA 2.0 scanner (late-bound COM, no interop assembly) and returns raw pages.</summary>
public static class ScannerService
{
    private const string PngFormat = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";
    private const string BmpFormat = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";
    private const string JpegFormat = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";
    private const uint PaperEmpty = 0x80210003;

    // WIA property ids
    private const int DocumentHandlingCapabilities = 3086;
    private const int DocumentHandlingStatus = 3087;
    private const int DocumentHandlingSelect = 3088;
    private const int Pages = 3096;
    private const int CurrentIntent = 6146;
    private const int HorizontalResolution = 6147;
    private const int VerticalResolution = 6148;
    private const int HorizontalStart = 6149;
    private const int VerticalStart = 6150;
    private const int HorizontalExtent = 6151;
    private const int VerticalExtent = 6152;
    private const int HorizontalPageSize = 3098; // WIA_DPS_PAGE_WIDTH
    private const int VerticalPageSize = 3099;   // WIA_DPS_PAGE_HEIGHT

    /// <summary>Paper size in millimetres (portrait).</summary>
    public static (double Width, double Height)? Millimetres(PaperSize paper) => paper switch
    {
        PaperSize.A4 => (210, 297),
        PaperSize.A5 => (148, 210),
        PaperSize.A6 => (105, 148),
        PaperSize.B5 => (176, 250),
        PaperSize.Letter => (215.9, 279.4),
        PaperSize.Legal => (215.9, 355.6),
        _ => null,
    };

    public static Task<List<ScannerInfo>> ListScannersAsync()
    {
        var completion = new TaskCompletionSource<List<ScannerInfo>>();
        var thread = new Thread(() =>
        {
            try { completion.SetResult(ListScanners()); }
            catch (Exception) { completion.SetResult([]); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private static List<ScannerInfo> ListScanners()
    {
        var result = new List<ScannerInfo>();
        var managerType = Type.GetTypeFromProgID("WIA.DeviceManager");
        if (managerType is null) return result;
        dynamic manager = Activator.CreateInstance(managerType)!;
        int count = manager.DeviceInfos.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic info = manager.DeviceInfos.Item(i);
            if ((int)info.Type != 1) continue;
            result.Add(new ScannerInfo((string)info.DeviceID, (string)info.Properties.Item("Name").Value));
        }
        return result;
    }

    private const int FeederFlag = 1;
    private const int FlatbedFlag = 2;
    private const int DuplexFlag = 4;
    private const int FeedReady = 1;
    private const int DeviceNameId = 7;
    private const int MaxPages = 500; // guard against a driver that never reports an empty tray

    /// <param name="cancel">Checked before each page: a page already being transferred finishes, then the scan stops.</param>
    public static Task<ScanResult> ScanAsync(ScanOptions options, IProgress<int>? progress, CancellationToken cancel = default)
    {
        var completion = new TaskCompletionSource<ScanResult>();
        // WIA is COM: run it on its own STA thread so the UI never blocks.
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Scan(options, progress, cancel)); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    private static ScanResult Scan(ScanOptions options, IProgress<int>? progress, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        dynamic device = Connect(options.DeviceId);
        var capabilities = GetInt(device.Properties, DocumentHandlingCapabilities) ?? FlatbedFlag;
        var hasFeeder = (capabilities & FeederFlag) != 0;
        var hasFlatbed = (capabilities & FlatbedFlag) != 0;
        var hasDuplex = (capabilities & DuplexFlag) != 0;

        var useFeeder = options.Source switch
        {
            ScanSource.Feeder => true,
            ScanSource.Flatbed => false,
            _ => hasFeeder && ((GetInt(device.Properties, DocumentHandlingStatus) ?? 0) & FeedReady) != 0,
        };
        if (useFeeder && !hasFeeder) throw new InvalidOperationException("This scanner has no document feeder.");
        if (!useFeeder && !hasFlatbed && hasFeeder) useFeeder = true;

        var deviceName = (string?)FindProperty(device.Properties, DeviceNameId)?.Value ?? "";
        if (options.UseTwain)
        {
            // WIA starts a new scan for every sheet (about 3 s each on feeders like the AD380); the scanner's TWAIN
            // driver feeds the whole stack in one pass. Let go of the WIA device first so the two don't fight over USB.
            Marshal.FinalReleaseComObject((object)device);
            var viaTwain = ScanWithTwain(deviceName, options, useFeeder, progress, cancel);
            if (viaTwain is not null) return viaTwain;
            device = Connect(options.DeviceId);
        }

        var hardwareDuplex = options.Duplex && useFeeder && hasDuplex;
        Log.Info($"Scan started: {options}; device capabilities={capabilities}, using {(useFeeder ? "feeder" : "flatbed")}, hardware duplex={hardwareDuplex}");
        if (hasFeeder || hasFlatbed)
        {
            var select = useFeeder ? FeederFlag : FlatbedFlag;
            if (hardwareDuplex) select |= DuplexFlag;
            SetInt(device.Properties, DocumentHandlingSelect, select);
        }
        // 0 = scan the whole stack in one go. Asking for one page at a time makes the feeder stop and restart between
        // sheets, which is several times slower. If the driver refuses, fall back to checking the tray after each page.
        // Drivers disagree on how to ask for "the whole stack": 0, a large number, or nothing but one page at a time.
        // Each is tried in turn when the previous one makes the first page fail (see the catch below).
        var pageCounts = new List<int>();
        if (useFeeder) pageCounts.AddRange([0, MaxPagesRequest(device.Properties), 1]);
        var pageIndex = 0;
        var continuous = false;
        void ApplyPageCount()
        {
            while (pageIndex < pageCounts.Count && !SetInt(device.Properties, Pages, pageCounts[pageIndex])) pageIndex++;
            continuous = pageIndex < pageCounts.Count && pageCounts[pageIndex] != 1;
        }
        ApplyPageCount();

        dynamic item = device.Items.Item(1);
        SetInt(item.Properties, HorizontalResolution, options.Dpi);
        SetInt(item.Properties, VerticalResolution, options.Dpi);
        SetInt(item.Properties, CurrentIntent, options.Color switch
        {
            ScanColor.Gray => 2,
            ScanColor.BlackWhite => 4,
            _ => 1,
        });
        var paperApplied = ApplyPaperSize(device, item, options);

        // Drivers produce JPEG far faster than PNG; only 1-bit scans need PNG to stay crisp (and are small anyway).
        var jpeg = options.Fast || options.Color != ScanColor.BlackWhite;
        // Some drivers reject a format ("The parameter is incorrect"); the list is the order we fall back through.
        var formats = new List<(string Guid, string Extension)>();
        if (jpeg) formats.Add((JpegFormat, ".jpg"));
        formats.Add((PngFormat, ".png"));
        formats.Add((BmpFormat, ".bmp"));
        var formatIndex = 0;

        // Looked up once: finding it again after every page means dozens of slow COM calls per sheet.
        dynamic? statusProperty = useFeeder ? FindProperty(device.Properties, DocumentHandlingStatus) : null;
        var watch = new System.Diagnostics.Stopwatch();

        var pages = new List<byte[]>();
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var (format, extension) = formats[formatIndex];
                watch.Restart();
                dynamic image = item.Transfer(format);
                var transferred = watch.ElapsedMilliseconds;
                if (pages.Count == 0)
                {
                    try { Log.Info($"Driver delivered {(string)image.FileExtension} ({(string)image.FormatID}), {(int)image.Width}x{(int)image.Height} px, asked for {extension}"); }
                    catch (Exception) { }
                }
                pages.Add(ReadImage(image, extension));
                Log.Info($"Page {pages.Count}: scanner transfer {transferred} ms, saving to memory {watch.ElapsedMilliseconds - transferred} ms, {pages[^1].Length / 1024} KB");
                progress?.Report(pages.Count);
            }
            catch (COMException ex) when ((uint)ex.HResult == PaperEmpty)
            {
                if (pages.Count == 0) throw new InvalidOperationException("The document feeder is empty. Load the paper and try again.");
                break;
            }
            catch (Exception ex) when (pages.Count == 0 && IsInvalidArgument(ex))
            {
                // First page refused: undo the speed-ups one at a time, then try other file formats.
                var requested = pageIndex < pageCounts.Count ? pageCounts[pageIndex] : -1;
                Log.Info($"First page refused ({ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message.Trim()}) with {formats[formatIndex].Extension}, pages requested={requested}");
                if (pageIndex + 1 < pageCounts.Count)
                {
                    pageIndex++;
                    ApplyPageCount();
                }
                else if (formatIndex + 1 < formats.Count) formatIndex++;
                else throw new InvalidOperationException("The scanner didn't accept these settings. Try page size Automatic, or a different resolution or colour mode.");
                continue;
            }

            if (!useFeeder) break;
            if (pages.Count >= MaxPages) break;

            // Stop when the tray is empty. Not every driver reports "paper empty" after the last sheet in all-pages mode,
            // so without this check the scan would just keep going.
            bool paperLeft;
            try { paperLeft = statusProperty is null ? continuous : (((int)statusProperty.Value) & FeedReady) != 0; }
            catch (Exception) { paperLeft = false; }
            if (!paperLeft) break;
        }
        Log.Info($"Scan finished: {pages.Count} page(s) as {formats[formatIndex].Extension}, pages requested={(pageIndex < pageCounts.Count ? pageCounts[pageIndex] : -1)}, page size applied={paperApplied}");
        return new ScanResult(pages, hardwareDuplex, paperApplied);
    }

    /// <summary>Scans through the scanner's TWAIN driver. Returns null if there is none or it failed, so the caller can use WIA instead.</summary>
    private static ScanResult? ScanWithTwain(string deviceName, ScanOptions options, bool useFeeder, IProgress<int>? progress, CancellationToken cancel)
    {
        string? source;
        try { source = TwainScanner.FindSource(deviceName); }
        catch (Exception ex) { Log.Error("Looking for a TWAIN driver", ex); return null; }
        if (source is null)
        {
            Log.Info($"No TWAIN driver found for '{deviceName}'; using WIA.");
            return null;
        }

        foreach (var jpegFile in new[] { true, false })
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                Log.Info($"TWAIN scan started with '{source}' (scanner makes JPEG: {jpegFile})");
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var result = TwainScanner.Scan(source, options, useFeeder, jpegFile, progress, cancel);
                if (result.Pages.Count == 0)
                    throw new InvalidOperationException(useFeeder
                        ? "The document feeder is empty. Load the paper and try again."
                        : "The scanner returned no page.");
                Log.Info($"TWAIN scan finished: {result.Pages.Count} page(s) in {watch.ElapsedMilliseconds} ms, double-sided={result.Duplex}, page size applied={result.PaperApplied}");
                return new ScanResult(result.Pages, result.Duplex, result.PaperApplied);
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("The document feeder is empty", StringComparison.Ordinal)) { throw; }
            catch (Exception ex)
            {
                Log.Error($"TWAIN scan (scanner makes JPEG: {jpegFile})", ex);
            }
        }
        Log.Info("TWAIN did not work; using WIA instead.");
        return null;
    }

    /// <summary>"The parameter is incorrect" (E_INVALIDARG) arrives as ArgumentException or as a COMException, depending on the driver.</summary>
    private static bool IsInvalidArgument(Exception ex) =>
        ex is ArgumentException || (ex is COMException c && (uint)c.HResult == 0x80070057);

    /// <summary>Limits the scan area to the chosen paper size, clamped to what the scanner can do.</summary>
    private static bool ApplyPaperSize(dynamic device, dynamic item, ScanOptions options)
    {
        if (Millimetres(options.Paper) is not var (width, height)) return true;

        // Feeders are told the sheet size (thousandths of an inch); flatbeds get a scan window in pixels.
        SetInt(device.Properties, HorizontalPageSize, (int)Math.Round(width / 25.4 * 1000));
        SetInt(device.Properties, VerticalPageSize, (int)Math.Round(height / 25.4 * 1000));

        SetInt(item.Properties, HorizontalStart, 0);
        SetInt(item.Properties, VerticalStart, 0);
        var wide = SetInt(item.Properties, HorizontalExtent, (int)Math.Round(width / 25.4 * options.Dpi), clampToMax: true);
        var tall = SetInt(item.Properties, VerticalExtent, (int)Math.Round(height / 25.4 * options.Dpi), clampToMax: true);
        return wide && tall;
    }

    private static dynamic Connect(string deviceId)
    {
        var managerType = Type.GetTypeFromProgID("WIA.DeviceManager")
            ?? throw new InvalidOperationException("Windows Image Acquisition (WIA) is not available on this PC.");
        dynamic manager = Activator.CreateInstance(managerType)!;

        var scanners = new List<dynamic>();
        int count = manager.DeviceInfos.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic info = manager.DeviceInfos.Item(i);
            if ((int)info.Type == 1) scanners.Add(info);
        }

        if (scanners.Count == 0) throw new InvalidOperationException("No scanner found. Check that it is switched on and connected.");
        if (deviceId.Length > 0)
        {
            foreach (var info in scanners)
                if ((string)info.DeviceID == deviceId) return info.Connect();
            throw new InvalidOperationException("The selected scanner is not connected. Pick another one or press refresh.");
        }
        if (scanners.Count == 1) return scanners[0].Connect();

        var dialogType = Type.GetTypeFromProgID("WIA.CommonDialog")!;
        dynamic dialog = Activator.CreateInstance(dialogType)!;
        dynamic? chosen = dialog.ShowSelectDevice(1, true, false);
        if (chosen is null) throw new OperationCanceledException();
        return chosen;
    }

    private static byte[] ReadImage(dynamic image, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sscanner-{Guid.NewGuid():N}{extension}");
        try
        {
            image.SaveFile(path);
            return File.ReadAllBytes(path);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static int? GetInt(dynamic properties, int id)
    {
        int count = properties.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic p = properties.Item(i);
            if ((int)p.PropertyID != id) continue;
            try { return (int)p.Value; } catch (Exception) { return null; }
        }
        return null;
    }

    /// <summary>A large "number of pages" the driver accepts: its own maximum if it reports one, else 999.</summary>
    private static int MaxPagesRequest(dynamic properties)
    {
        try
        {
            dynamic? p = FindProperty(properties, Pages);
            if (p is null) return 999;
            Log.Info($"Pages property: subtype={(int)p.SubType}, value={(int)p.Value}, min={(int)p.SubTypeMin}, max={(int)p.SubTypeMax}");
            if ((int)p.SubType == 1) return Math.Clamp((int)p.SubTypeMax, 2, 999); // 1 = range
        }
        catch (Exception) { }
        return 999;
    }

    private static dynamic? FindProperty(dynamic properties, int id)
    {
        int count = properties.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic p = properties.Item(i);
            if ((int)p.PropertyID == id) return p;
        }
        return null;
    }

    /// <summary>Sets a property if the device exposes it; drivers differ, so missing ones are skipped.</summary>
    private static bool SetInt(dynamic properties, int id, int value, bool clampToMax = false)
    {
        int count = properties.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic p = properties.Item(i);
            if ((int)p.PropertyID != id) continue;
            try
            {
                if (clampToMax && (int)p.SubType == 1) value = Math.Min(value, (int)p.SubTypeMax); // 1 = range
                p.Value = value;
                return true;
            }
            // Read-only or unsupported values: drivers report "access denied" as UnauthorizedAccessException, not COMException.
            catch (Exception ex)
            {
                Log.Info($"Scanner property {id} could not be set to {value}: {ex.GetType().Name} (0x{ex.HResult:X8}) {ex.Message}");
                return false;
            }
        }
        return false;
    }

    public static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "Scan cancelled.",
        InvalidOperationException => ex.Message,
        COMException c => (uint)c.HResult switch
        {
            0x80210001 => "The scanner reported an unknown error.",
            0x80210002 => "Paper jam in the scanner.",
            0x80210004 => "Scanner problem. Check the paper and try again.",
            0x80210005 => "The scanner is offline or switched off.",
            0x80210006 => "The scanner is busy. Try again in a moment.",
            0x80210016 => "The scanner cover is open.",
            _ => $"Scanner error ({c.HResult:X8}).",
        },
        _ => ex.Message,
    };
}
