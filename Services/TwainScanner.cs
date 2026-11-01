using System.Runtime.InteropServices;

namespace SScanner.Services;

/// <summary>
/// Scans through the scanner's TWAIN driver (64-bit TWAIN DSM). Unlike WIA, a TWAIN feeder scan hands back the
/// whole stack in one pass, so the feeder doesn't stop and restart between sheets. Pages arrive as BMP files.
/// Must run on an STA thread: it creates a hidden window and runs a message loop for the driver.
/// </summary>
public static class TwainScanner
{
    // ---- TWAIN constants ----
    private const uint DG_CONTROL = 1, DG_IMAGE = 2;
    private const ushort DAT_CAPABILITY = 1, DAT_EVENT = 2, DAT_IDENTITY = 3, DAT_PARENT = 4, DAT_PENDINGXFERS = 5,
        DAT_USERINTERFACE = 9, DAT_IMAGENATIVEXFER = 0x0104;
    private const ushort MSG_GET = 1, MSG_GETFIRST = 4, MSG_GETNEXT = 5, MSG_SET = 6, MSG_RESET = 7,
        MSG_XFERREADY = 0x101, MSG_CLOSEDSREQ = 0x102, MSG_CLOSEDSOK = 0x103, MSG_OPENDSM = 0x301, MSG_CLOSEDSM = 0x302,
        MSG_OPENDS = 0x401, MSG_CLOSEDS = 0x402, MSG_DISABLEDS = 0x501, MSG_ENABLEDS = 0x502, MSG_PROCESSEVENT = 0x601,
        MSG_ENDXFER = 0x701;
    private const ushort RC_SUCCESS = 0, RC_DSEVENT = 4, RC_XFERDONE = 6, RC_ENDOFLIST = 7;
    private const ushort DAT_SETUPFILEXFER = 7, DAT_IMAGEFILEXFER = 0x0105;
    private const ushort TWFF_JFIF = 4;   // JPEG file format
    private const ushort TWSX_FILE = 1;   // transfer through a file (the scanner compresses to JPEG itself)
    private const ushort TWON_ONEVALUE = 5;
    private const ushort TWTY_UINT16 = 4, TWTY_BOOL = 6, TWTY_FIX32 = 7, TWTY_INT16 = 3;

    private const ushort CAP_XFERCOUNT = 0x0001, ICAP_PIXELTYPE = 0x0101, ICAP_UNITS = 0x0102, ICAP_XFERMECH = 0x0103,
        ICAP_XRESOLUTION = 0x1118, ICAP_YRESOLUTION = 0x1119, ICAP_SUPPORTEDSIZES = 0x1122,
        CAP_FEEDERENABLED = 0x1002, CAP_AUTOFEED = 0x1007, CAP_INDICATORS = 0x100B, CAP_DUPLEXENABLED = 0x1013;

    private const int WatchdogSeconds = 90; // give up if the driver goes silent for this long

    // ---- Interop ----
    [StructLayout(LayoutKind.Sequential, Pack = 2, CharSet = CharSet.Ansi)]
    private struct TW_IDENTITY
    {
        public uint Id;
        public ushort VersionMajor, VersionMinor, Language, Country;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)] public string VersionInfo;
        public ushort ProtocolMajor, ProtocolMinor;
        public uint SupportedGroups;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)] public string Manufacturer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)] public string ProductFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)] public string ProductName;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct TW_CAPABILITY { public ushort Cap; public ushort ConType; public IntPtr hContainer; }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct TW_ONEVALUE { public ushort ItemType; public uint Item; }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct TW_PENDINGXFERS { public ushort Count; public uint EOJ; }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct TW_USERINTERFACE { public ushort ShowUI; public ushort ModalUI; public IntPtr hParent; }

    [StructLayout(LayoutKind.Sequential, Pack = 2, CharSet = CharSet.Ansi)]
    private struct TW_SETUPFILEXFER
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string FileName;
        public ushort Format;
        public short VRefNum;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    [DllImport("TWAINDSM.dll", EntryPoint = "DSM_Entry")]
    private static extern ushort DsmEntry(IntPtr origin, IntPtr dest, uint dg, ushort dat, ushort msg, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);

    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr h);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr h);

    private const uint GMEM_MOVEABLE = 2;
    private const uint WS_POPUP = 0x80000000;

    /// <summary>Names of the installed TWAIN sources (empty if the TWAIN manager isn't there).</summary>
    public static List<string> ListSources()
    {
        var names = new List<string>();
        var hwnd = IntPtr.Zero;
        try
        {
            hwnd = CreateWindowEx(0, "STATIC", "", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            using var session = new Session();
            session.OpenManager(hwnd);
            names.AddRange(session.Sources().Select(s => s.ProductName));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            Log.Info($"TWAIN not available: {ex.Message}");
        }
        finally
        {
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
        }
        return names;
    }

    /// <summary>Returns the first TWAIN source whose name contains <paramref name="hint"/> (e.g. the WIA scanner name), or null.</summary>
    public static string? FindSource(string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        return ListSources().FirstOrDefault(n => n.Contains(hint, StringComparison.OrdinalIgnoreCase) || hint.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Scans the feeder (or flatbed) without showing the driver's own window. Returns each page as BMP bytes.</summary>
    /// <param name="Duplex">The driver accepted double-sided scanning (pages arrive front, back, front, back…).</param>
    /// <param name="PaperApplied">False if a page size was requested but the driver would not take it.</param>
    public sealed record Result(List<byte[]> Pages, bool Duplex, bool PaperApplied);

    /// <param name="jpegFile">Have the scanner compress each page to JPEG and hand it over as a file (much less data over USB). Colour and grey only.</param>
    public static Result Scan(string sourceName, ScanOptions options, bool useFeeder, bool jpegFile, IProgress<int>? progress, CancellationToken cancel)
    {
        using var session = new Session();
        var hwnd = CreateWindowEx(0, "STATIC", "", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create a window for the TWAIN driver.");
        try
        {
            session.OpenManager(hwnd);
            var sources = session.Sources();
            var index = sources.FindIndex(s => s.ProductName == sourceName);
            if (index < 0) throw new InvalidOperationException($"The TWAIN driver '{sourceName}' was not found.");
            session.OpenSource(sources[index]);
            var fileMode = jpegFile && options.Color != ScanColor.BlackWhite && session.SetCap(ICAP_XFERMECH, TWTY_UINT16, TWSX_FILE);
            session.FileMode = fileMode;
            Configure(session, options, useFeeder, fileMode);
            var pages = session.Run(hwnd, progress, cancel);
            return new Result(pages, session.DuplexApplied, session.PaperApplied);
        }
        finally
        {
            DestroyWindow(hwnd);
        }
    }

    private static void Configure(Session s, ScanOptions o, bool useFeeder, bool fileMode)
    {
        if (!fileMode) s.SetCap(ICAP_XFERMECH, TWTY_UINT16, 0);  // native (DIB) transfer
        s.SetCap(ICAP_UNITS, TWTY_UINT16, 0);                    // inches
        s.SetCap(CAP_INDICATORS, TWTY_BOOL, 0);                  // no driver progress window
        s.SetCap(ICAP_PIXELTYPE, TWTY_UINT16, o.Color switch { ScanColor.BlackWhite => 0u, ScanColor.Gray => 1u, _ => 2u });
        var dpi = (uint)(o.Dpi & 0xFFFF);                        // Fix32: whole part in the low word, fraction 0
        s.SetCap(ICAP_XRESOLUTION, TWTY_FIX32, dpi);
        s.SetCap(ICAP_YRESOLUTION, TWTY_FIX32, dpi);
        s.PaperApplied = SupportedSize(o.Paper) is not { } size || s.SetCap(ICAP_SUPPORTEDSIZES, TWTY_UINT16, size);
        if (useFeeder)
        {
            s.SetCap(CAP_FEEDERENABLED, TWTY_BOOL, 1);
            s.SetCap(CAP_AUTOFEED, TWTY_BOOL, 1);
            s.SetCap(CAP_XFERCOUNT, TWTY_INT16, unchecked((uint)(ushort)(short)-1)); // as many pages as are loaded
            s.DuplexApplied = s.SetCap(CAP_DUPLEXENABLED, TWTY_BOOL, o.Duplex ? 1u : 0u) && o.Duplex;
        }
        else
        {
            s.SetCap(CAP_FEEDERENABLED, TWTY_BOOL, 0);
            s.SetCap(CAP_XFERCOUNT, TWTY_INT16, 1);
        }
    }

    private static uint? SupportedSize(PaperSize paper) => paper switch
    {
        PaperSize.A4 => 1,       // TWSS_A4
        PaperSize.B5 => 2,       // TWSS_JISB5
        PaperSize.Letter => 3,   // TWSS_USLETTER
        PaperSize.Legal => 4,    // TWSS_USLEGAL
        PaperSize.A5 => 5,       // TWSS_A5
        PaperSize.A6 => 14,      // TWSS_A6
        _ => null,
    };

    /// <summary>One TWAIN conversation: opens the manager and a source, and closes them again on dispose.</summary>
    private sealed class Session : IDisposable
    {
        private readonly IntPtr origin = Marshal.AllocHGlobal(Marshal.SizeOf<TW_IDENTITY>());
        private readonly IntPtr ds = Marshal.AllocHGlobal(Marshal.SizeOf<TW_IDENTITY>());
        private readonly IntPtr scratch = Marshal.AllocHGlobal(256);
        private readonly IntPtr ui = Marshal.AllocHGlobal(Marshal.SizeOf<TW_USERINTERFACE>());
        private bool managerOpen, sourceOpen, enabled;

        public bool FileMode { get; set; }
        public bool DuplexApplied { get; set; }
        public bool PaperApplied { get; set; } = true;

        public Session()
        {
            Marshal.StructureToPtr(new TW_USERINTERFACE(), ui, false);
            var id = new TW_IDENTITY
            {
                Id = 0,
                VersionMajor = 1,
                VersionMinor = 0,
                Language = 13,   // English (USA)
                Country = 1,
                VersionInfo = "1.0",
                ProtocolMajor = 2,
                ProtocolMinor = 3,
                SupportedGroups = DG_CONTROL | DG_IMAGE | 0x20000000, // 0x20000000 = DF_APP2: use the 2.x manager
                Manufacturer = "Arman Soltani",
                ProductFamily = "Every Scanner",
                ProductName = "Every Scanner",
            };
            Marshal.StructureToPtr(id, origin, false);
            Marshal.StructureToPtr(new TW_IDENTITY { VersionInfo = "", Manufacturer = "", ProductFamily = "", ProductName = "" }, ds, false);
        }

        private ushort Call(IntPtr dest, uint dg, ushort dat, ushort msg, IntPtr data) => DsmEntry(origin, dest, dg, dat, msg, data);

        public void OpenManager(IntPtr hwnd)
        {
            Marshal.WriteIntPtr(scratch, hwnd);
            var rc = Call(IntPtr.Zero, DG_CONTROL, DAT_PARENT, MSG_OPENDSM, scratch);
            if (rc != RC_SUCCESS) throw new InvalidOperationException($"The TWAIN manager could not be opened (code {rc}).");
            managerOpen = true;
        }

        public List<TW_IDENTITY> Sources()
        {
            var list = new List<TW_IDENTITY>();
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<TW_IDENTITY>());
            try
            {
                Marshal.StructureToPtr(new TW_IDENTITY { VersionInfo = "", Manufacturer = "", ProductFamily = "", ProductName = "" }, buffer, false);
                var rc = Call(IntPtr.Zero, DG_CONTROL, DAT_IDENTITY, MSG_GETFIRST, buffer);
                while (rc == RC_SUCCESS)
                {
                    list.Add(Marshal.PtrToStructure<TW_IDENTITY>(buffer));
                    rc = Call(IntPtr.Zero, DG_CONTROL, DAT_IDENTITY, MSG_GETNEXT, buffer);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return list;
        }

        public void OpenSource(TW_IDENTITY source)
        {
            Marshal.StructureToPtr(source, ds, false);
            var rc = Call(IntPtr.Zero, DG_CONTROL, DAT_IDENTITY, MSG_OPENDS, ds);
            if (rc != RC_SUCCESS) throw new InvalidOperationException($"The scanner's TWAIN driver could not be opened (code {rc}). Is another program using it?");
            sourceOpen = true;
        }

        /// <summary>Sets a capability; drivers reject some, which is fine, so failures are only logged.</summary>
        public bool SetCap(ushort cap, ushort itemType, uint value)
        {
            var container = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Marshal.SizeOf<TW_ONEVALUE>());
            var locked = GlobalLock(container);
            Marshal.StructureToPtr(new TW_ONEVALUE { ItemType = itemType, Item = value }, locked, false);
            GlobalUnlock(container);

            var capability = new TW_CAPABILITY { Cap = cap, ConType = TWON_ONEVALUE, hContainer = container };
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<TW_CAPABILITY>());
            try
            {
                Marshal.StructureToPtr(capability, buffer, false);
                var rc = Call(ds, DG_CONTROL, DAT_CAPABILITY, MSG_SET, buffer);
                if (rc != RC_SUCCESS) Log.Info($"TWAIN: capability 0x{cap:X4} = {value} was not accepted (code {rc})");
                return rc == RC_SUCCESS;
            }
            finally
            {
                GlobalFree(container);
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>Starts the scan without the driver's window and collects every page until the driver is done.</summary>
        public List<byte[]> Run(IntPtr hwnd, IProgress<int>? progress, CancellationToken cancel)
        {
            var pages = new List<byte[]>();
            var msgPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MSG>());
            var evt = Marshal.AllocHGlobal(32);
            try
            {
                Marshal.StructureToPtr(new TW_USERINTERFACE { ShowUI = 0, ModalUI = 0, hParent = hwnd }, ui, false);
                var rc = Call(ds, DG_CONTROL, DAT_USERINTERFACE, MSG_ENABLEDS, ui);
                if (rc != RC_SUCCESS) throw new InvalidOperationException($"The scanner's TWAIN driver would not start scanning (code {rc}).");
                enabled = true;

                SetTimer(hwnd, (UIntPtr)1, 500, IntPtr.Zero); // wakes the loop so cancelling and the watchdog work
                var lastActivity = DateTime.UtcNow;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var done = false;
                while (!done && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (cancel.IsCancellationRequested) throw new OperationCanceledException();
                    if ((DateTime.UtcNow - lastActivity).TotalSeconds > WatchdogSeconds)
                        throw new InvalidOperationException("The scanner's driver stopped responding.");

                    Marshal.StructureToPtr(msg, msgPtr, false);
                    Marshal.WriteIntPtr(evt, 0, msgPtr);
                    Marshal.WriteInt16(evt, IntPtr.Size, 0);
                    var result = Call(ds, DG_CONTROL, DAT_EVENT, MSG_PROCESSEVENT, evt);
                    if (result == RC_DSEVENT)
                    {
                        lastActivity = DateTime.UtcNow;
                        switch ((ushort)Marshal.ReadInt16(evt, IntPtr.Size))
                        {
                            case MSG_XFERREADY:
                                Transfer(pages, progress, cancel, watch);
                                lastActivity = DateTime.UtcNow;
                                done = true;
                                break;
                            case MSG_CLOSEDSREQ:
                            case MSG_CLOSEDSOK:
                                done = true;
                                break;
                        }
                    }
                    else
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                }
                return pages;
            }
            finally
            {
                Marshal.FreeHGlobal(msgPtr);
                Marshal.FreeHGlobal(evt);
            }
        }

        private void Transfer(List<byte[]> pages, IProgress<int>? progress, CancellationToken cancel, System.Diagnostics.Stopwatch watch)
        {
            var handle = Marshal.AllocHGlobal(IntPtr.Size);
            var pending = Marshal.AllocHGlobal(Marshal.SizeOf<TW_PENDINGXFERS>());
            try
            {
                while (true)
                {
                    Marshal.WriteIntPtr(handle, IntPtr.Zero);
                    var before = watch.ElapsedMilliseconds;
                    ushort rc;
                    string? file = null;
                    if (FileMode)
                    {
                        file = Path.Combine(Path.GetTempPath(), $"sscanner-twain-{Guid.NewGuid():N}.jpg");
                        var setup = Marshal.AllocHGlobal(Marshal.SizeOf<TW_SETUPFILEXFER>());
                        try
                        {
                            Marshal.StructureToPtr(new TW_SETUPFILEXFER { FileName = file, Format = TWFF_JFIF }, setup, false);
                            var setupRc = Call(ds, DG_CONTROL, DAT_SETUPFILEXFER, MSG_SET, setup);
                            if (setupRc != RC_SUCCESS)
                            {
                                Call(ds, DG_CONTROL, DAT_PENDINGXFERS, MSG_RESET, pending); // leave the transfer state cleanly
                                throw new InvalidOperationException($"The scanner's driver would not save JPEG files (code {setupRc}).");
                            }
                        }
                        finally { Marshal.FreeHGlobal(setup); }
                        rc = Call(ds, DG_IMAGE, DAT_IMAGEFILEXFER, MSG_GET, IntPtr.Zero);
                    }
                    else
                    {
                        rc = Call(ds, DG_IMAGE, DAT_IMAGENATIVEXFER, MSG_GET, handle);
                    }

                    if (rc == RC_XFERDONE)
                    {
                        if (file is null) pages.Add(DibToBmp(Marshal.ReadIntPtr(handle)));
                        else
                        {
                            pages.Add(File.ReadAllBytes(file));
                            try { File.Delete(file); } catch (IOException) { }
                        }
                        Log.Info($"TWAIN page {pages.Count}: {watch.ElapsedMilliseconds - before} ms, {pages[^1].Length / 1024} KB{(FileMode ? " (JPEG from scanner)" : "")}");
                        progress?.Report(pages.Count);
                    }
                    else
                    {
                        Log.Info($"TWAIN: page transfer ended with code {rc}");
                    }

                    var ended = Call(ds, DG_CONTROL, DAT_PENDINGXFERS, MSG_ENDXFER, pending);
                    if (ended != RC_SUCCESS) break;
                    var remaining = Marshal.PtrToStructure<TW_PENDINGXFERS>(pending).Count;
                    if (remaining == 0 || rc != RC_XFERDONE) break;
                    if (cancel.IsCancellationRequested)
                    {
                        Call(ds, DG_CONTROL, DAT_PENDINGXFERS, MSG_RESET, pending); // drop what's left
                        throw new OperationCanceledException();
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(handle);
                Marshal.FreeHGlobal(pending);
            }
        }

        /// <summary>Wraps the driver's in-memory bitmap with a file header so it can be decoded like any .bmp file.</summary>
        private static byte[] DibToBmp(IntPtr dibHandle)
        {
            var size = (int)GlobalSize(dibHandle);
            var locked = GlobalLock(dibHandle);
            var dib = new byte[size];
            Marshal.Copy(locked, dib, 0, size);
            GlobalUnlock(dibHandle);
            GlobalFree(dibHandle);

            int headerSize = BitConverter.ToInt32(dib, 0);
            int bitCount = BitConverter.ToInt16(dib, 14);
            int compression = BitConverter.ToInt32(dib, 16);
            int colorsUsed = BitConverter.ToInt32(dib, 32);
            var palette = bitCount <= 8 ? (colorsUsed != 0 ? colorsUsed : 1 << bitCount) * 4 : 0;
            if (compression == 3 && headerSize == 40) palette += 12; // BI_BITFIELDS masks

            var bmp = new byte[14 + size];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes(14 + headerSize + palette).CopyTo(bmp, 10);
            Buffer.BlockCopy(dib, 0, bmp, 14, size);
            return bmp;
        }

        public void Dispose()
        {
            try
            {
                if (enabled) Call(ds, DG_CONTROL, DAT_USERINTERFACE, MSG_DISABLEDS, ui);
                if (sourceOpen) Call(IntPtr.Zero, DG_CONTROL, DAT_IDENTITY, MSG_CLOSEDS, ds);
                if (managerOpen) Call(IntPtr.Zero, DG_CONTROL, DAT_PARENT, MSG_CLOSEDSM, scratch);
            }
            catch (Exception ex) when (ex is SEHException or AccessViolationException or DllNotFoundException) { }
            Marshal.FreeHGlobal(origin);
            Marshal.FreeHGlobal(ds);
            Marshal.FreeHGlobal(scratch);
            Marshal.FreeHGlobal(ui);
        }
    }
}
