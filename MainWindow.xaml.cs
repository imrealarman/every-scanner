using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SScanner.Models;
using SScanner.Services;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace SScanner;

public sealed partial class MainWindow : Window
{
    private static readonly int[] DpiChoices = [150, 200, 300, 600];
    private const int FastDpiCap = 200;

    private readonly ObservableCollection<ScanPage> pages = [];
    private readonly Settings settings = Settings.Load();
    private readonly ScanIndex index = new();
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private ScanPage? current;
    private string? toastPath;
    private bool busy;
    private bool loadingSettings = true; // stays true until the saved settings are in the controls, so load-time change events can't overwrite them
    private bool syncingSliders;
    private bool cropping;
    private bool dialogOpen;
    private int adjustVersion;
    private bool painting;
    private bool quadding;
    private ImageEnhancer.BrushKind brushKind;
    private double toneExposure = 30, eraseExposure = 100; // each kind remembers its own strength
    private int brushPending;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Every Scanner";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TopBar);
        AppWindow.Resize(new SizeInt32(1480, 920));
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon); // taskbar and Alt+Tab icon

        Filmstrip.ItemsSource = pages;
        pages.CollectionChanged += OnPagesChanged;
        CropArea.SelectionChanged += OnCropSelectionChanged;
        CropArea.ApplyRequested += () => _ = ApplyCropAsync();
        BrushArea.StrokeCompleted += OnBrushStroke;
        RotateGrips.DragStarted += () => rotateStart = StraightenSlider.Value;
        RotateGrips.Rotated += degrees =>
        {
            // Dragging clockwise turns the page clockwise; the straighten angle is counter-clockwise positive.
            StraightenSlider.Value = Math.Clamp(Math.Round(rotateStart - degrees, 1), StraightenSlider.Minimum, StraightenSlider.Maximum);
        };
        // handledEventsToo: the ScrollViewer and the crop overlay may already have handled the press.
        PreviewScroll.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PanPressed), true);
        PreviewScroll.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(PanMoved), true);
        PreviewScroll.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PanEnded), true);
        PreviewScroll.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => panning = false), true);
        Root.PreviewKeyDown += RootPreviewCompareDown;
        Root.PreviewKeyUp += RootPreviewCompareUp;
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) EndCompare(); };
        // handledEventsToo: the button itself swallows the press.
        CompareButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _ = BeginCompareAsync()), true);
        CompareButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => EndCompare()), true);
        CompareButton.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndCompare()), true);
        Root.PreviewKeyDown += RootPreviewSpace;
        Root.PreviewKeyDown += RootPreviewCropKey;
        Root.PreviewKeyUp += RootPreviewSpace;
        toastTimer.Tick += (_, _) => HideToast();
        saveTimer.Tick += (_, _) =>
        {
            saveTimer.Stop();
            settings.Save();
            if (settings.Format == OutputFormat.Pdf)
                foreach (var page in pages) _ = page.PdfBytes(settings.Quality); // quality changed: refresh the prepared copies
        };

        EnsureWorkspace();
        ApplyTheme();
        Root.ActualThemeChanged += (_, _) => { Palette.Apply(IsLight); StyleCaptionButtons(); UpdateUi(); };
        RegisterShortcuts();

        LoadSettingsIntoUi();
        RefreshWorkspaceList();
        EnsureDailyFolder();
        UpdateUi();
        _ = index.RefreshAsync(settings.Folder);

        // Start with focus on the page strip, not the search box, so the single-key shortcuts work straight away.
        Root.Loaded += async (_, _) =>
        {
            Filmstrip.Focus(FocusState.Programmatic);
            await RefreshScannersAsync();
        };

    }

    // ---- Theme ----

    private void ApplyTheme()
    {
        Root.RequestedTheme = settings.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        Palette.Apply(IsLight);
        StyleCaptionButtons();
    }

    /// <summary>Whether the light palette is in use: the chosen theme, or Windows' own setting for "System".</summary>
    private bool IsLight => settings.Theme switch
    {
        AppTheme.Light => true,
        AppTheme.Dark => false,
        _ => new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background).R > 128,
    };

    private void StyleCaptionButtons()
    {
        var light = IsLight;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = light ? ColorHelper.FromArgb(255, 0x22, 0x22, 0x22) : ColorHelper.FromArgb(255, 0xEA, 0xEA, 0xEA);
        bar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 0x9B, 0x9B, 0x9B);
        bar.ButtonHoverBackgroundColor = light ? ColorHelper.FromArgb(255, 0xE1, 0xE1, 0xE1) : ColorHelper.FromArgb(255, 0x3A, 0x3A, 0x3A);
        bar.ButtonHoverForegroundColor = light ? Colors.Black : Colors.White;
    }

    /// <summary>Looks up a palette brush (its colour already matches the current theme).</summary>
    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static Brush Accent => (Brush)Application.Current.Resources["AccentBrush"];

    private void ThemeClick(object sender, RoutedEventArgs e)
    {
        settings.Theme = Enum.Parse<AppTheme>((string)((Button)sender).Tag);
        settings.Save();
        ApplyTheme();
        UpdateUi();
    }

    private void CycleTheme()
    {
        settings.Theme = (AppTheme)(((int)settings.Theme + 1) % 3);
        settings.Save();
        ApplyTheme();
        UpdateUi();
        ShowToast($"Theme: {settings.Theme}");
    }

    // ---- Settings ----

    private List<ScannerInfo> scanners = [];
    private bool scannersLoaded;

    private static bool IsAdministrator =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    private async void RestartAsAdminClick(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var warning = pages.Count > 0 ? "\n\n" + "The pages you have scanned but not saved will be lost." : "";
        var answer = await ShowDialogAsync(new ContentDialog
        {
            Title = "Restart as administrator?",
            Content = "Every Scanner will close and open again with administrator rights, which some scanner drivers need before they accept settings such as the page size." + warning,
            PrimaryButtonText = "Restart",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        });
        if (answer != ContentDialogResult.Primary) return;

        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Exit();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Info($"Restart as administrator did not happen: {ex.Message}"); // most likely the user declined the Windows prompt
            ShowToast("Every Scanner was not restarted as administrator.");
        }
    }

    private async void RefreshScannersClick(object sender, RoutedEventArgs e)
    {
        await RefreshScannersAsync();
        ShowToast(scanners.Count == 0 ? "No scanners found." : string.Format("Found {0} scanner(s).", scanners.Count));
    }

    /// <summary>Fills the scanner list: automatic first, then every scanner Windows knows about.</summary>
    private async Task RefreshScannersAsync()
    {
        scanners = await ScannerService.ListScannersAsync();
        var wanted = settings.ScannerId;
        var names = new List<string> { "Automatic (ask if several)" };
        names.AddRange(scanners.Select(s => s.Name));
        var selected = scanners.FindIndex(s => s.Id == wanted) + 1;
        if (wanted.Length > 0 && selected == 0)
            names.Add("Saved scanner (not connected)");

        loadingSettings = true;
        ScannerCombo.Items.Clear();
        foreach (var name in names) ScannerCombo.Items.Add(name);
        ScannerCombo.SelectedIndex = selected > 0 ? selected : (wanted.Length > 0 ? names.Count - 1 : 0);
        loadingSettings = false;
        scannersLoaded = true;
    }

    private void LoadSettingsIntoUi()
    {
        loadingSettings = true;
        PaperCombo.SelectedIndex = (int)settings.Paper;
        TwainCheck.IsChecked = settings.UseTwain;
        AdminButton.Visibility = IsAdministrator ? Visibility.Collapsed : Visibility.Visible;
        SourceCombo.SelectedIndex = (int)settings.Source;
        ColorCombo.SelectedIndex = (int)settings.Color;
        DpiCombo.SelectedIndex = Math.Max(0, Array.IndexOf(DpiChoices, settings.Dpi));
        SkipBlankCheck.IsChecked = settings.SkipBlank;
        AutoStraightenCheck.IsChecked = settings.AutoStraighten;
        FastCheck.IsChecked = settings.Fast;
        SmartCheck.IsChecked = settings.SmartMode;
        DuplexCheck.IsChecked = settings.DoubleSided;
        QualitySlider.Value = settings.Quality;
        OcrCheck.IsChecked = settings.Ocr;
        DailyCheck.IsChecked = settings.DailyFolders;
        DateStyleCombo.SelectedIndex = (int)settings.DateStyle;
        loadingSettings = false;
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (loadingSettings) return;
        if (scannersLoaded)
        {
            var pick = ScannerCombo.SelectedIndex;
            if (pick == 0) settings.ScannerId = "";
            else if (pick <= scanners.Count) settings.ScannerId = scanners[pick - 1].Id;
        }
        settings.Paper = (PaperSize)Math.Max(0, PaperCombo.SelectedIndex);
        settings.Source = (ScanSource)Math.Max(0, SourceCombo.SelectedIndex);
        settings.Color = (ScanColor)Math.Max(0, ColorCombo.SelectedIndex);
        settings.Dpi = DpiChoices[Math.Max(0, DpiCombo.SelectedIndex)];
        settings.UseTwain = TwainCheck.IsChecked == true;
        settings.SkipBlank = SkipBlankCheck.IsChecked == true;
        settings.AutoStraighten = AutoStraightenCheck.IsChecked == true;
        settings.Fast = FastCheck.IsChecked == true;
        settings.SmartMode = SmartCheck.IsChecked == true;
        settings.DoubleSided = DuplexCheck.IsChecked == true;
        settings.Ocr = OcrCheck.IsChecked == true;
        settings.DailyFolders = DailyCheck.IsChecked == true;
        settings.DateStyle = (DateStyle)Math.Max(0, DateStyleCombo.SelectedIndex);
        settings.Save();
        EnsureDailyFolder();
        UpdateUi();
    }

    private void FormatClick(object sender, RoutedEventArgs e)
    {
        settings.Format = Enum.Parse<OutputFormat>((string)((Button)sender).Tag);
        settings.Save();
        UpdateUi();
    }

    private void QualityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (loadingSettings) return;
        settings.Quality = (int)QualitySlider.Value;
        QualityValue.Text = $"{settings.Quality}%";
        saveTimer.Stop();
        saveTimer.Start(); // sliders fire constantly while dragging; write once they settle
    }

    private async void ChangeFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        settings.Folder = folder.Path;
        settings.Save();
        EnsureDailyFolder();
        UpdateUi();
        _ = index.RefreshAsync(settings.Folder);
    }

    private void OpenFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = settings.TargetFolder(DateTime.Now);
        if (!Directory.Exists(folder)) folder = settings.Folder;
        Directory.CreateDirectory(folder);
        Process.Start("explorer.exe", $"\"{folder}\"");
    }

    /// <summary>With daily folders on, today's folder is created as soon as the app (or the option) is on.</summary>
    private void EnsureDailyFolder()
    {
        if (!settings.DailyFolders) return;
        try { Directory.CreateDirectory(settings.TargetFolder(DateTime.Now)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- Workspaces ----

    /// <summary>First run: store the current settings as the "Default" workspace.</summary>
    private void EnsureWorkspace()
    {
        if (WorkspaceStore.List().Count > 0 && WorkspaceStore.Load(settings.Workspace) is not null) return;
        if (WorkspaceStore.List().Count == 0)
        {
            settings.Workspace = "Default";
            WorkspaceStore.Save("Default", settings);
        }
        else if (WorkspaceStore.Load(settings.Workspace) is null)
        {
            settings.Workspace = WorkspaceStore.List()[0];
        }
        settings.Save();
    }

    private void RefreshWorkspaceList()
    {
        loadingSettings = true;
        WorkspaceCombo.ItemsSource = WorkspaceStore.List();
        WorkspaceCombo.SelectedItem = settings.Workspace;
        loadingSettings = false;
    }

    private void ApplyWorkspace(string name, Settings workspace)
    {
        settings.CopyFrom(workspace);
        settings.Workspace = name;
        settings.Save();
        LoadSettingsIntoUi();
        RefreshWorkspaceList();
        EnsureDailyFolder();
        UpdateUi();
        _ = index.RefreshAsync(settings.Folder);
    }

    private void WorkspaceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (loadingSettings || WorkspaceCombo.SelectedItem is not string name) return;
        // The combo reports its initial selection after loading; re-applying the active workspace would wipe unsaved changes.
        if (name == settings.Workspace) return;
        var workspace = WorkspaceStore.Load(name);
        if (workspace is null) { ShowToast("That workspace could not be read."); return; }
        ApplyWorkspace(name, workspace);
        ShowToast(string.Format("Workspace “{0}” loaded", name));
    }

    private void WorkspaceSaveClick(object sender, RoutedEventArgs e)
    {
        WorkspaceStore.Save(settings.Workspace, settings);
        ShowToast(string.Format("Saved settings to “{0}”", settings.Workspace));
    }

    private async void WorkspaceNewClick(object sender, RoutedEventArgs e)
    {
        var name = await AskTextAsync("New workspace", "Name", "Create", string.Format("Workspace {0}", WorkspaceStore.List().Count + 1));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = WorkspaceStore.UniqueName(name);
        settings.Workspace = name;
        WorkspaceStore.Save(name, settings);
        settings.Save();
        RefreshWorkspaceList();
        ShowToast(string.Format("Created workspace “{0}” from your current settings", name));
    }

    private void WorkspaceDeleteClick(object sender, RoutedEventArgs e)
    {
        var all = WorkspaceStore.List();
        if (all.Count <= 1) { ShowToast("Keep at least one workspace."); return; }
        var deleted = settings.Workspace;
        WorkspaceStore.Delete(deleted);
        var next = WorkspaceStore.List()[0];
        ApplyWorkspace(next, WorkspaceStore.Load(next) ?? new Settings());
        ShowToast(string.Format("Deleted “{0}”", deleted));
    }

    private async void WorkspaceExportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedFileName = settings.Workspace };
        picker.FileTypeChoices.Add("Every Scanner workspace", [WorkspaceStore.Extension]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            await File.WriteAllTextAsync(file.Path, WorkspaceStore.Serialize(settings, settings.Workspace));
            ShowToast(string.Format("Exported “{0}”", settings.Workspace), file.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(string.Format("Could not export: {0}", ex.Message));
        }
    }

    private async void WorkspaceImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(WorkspaceStore.Extension);
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            var imported = WorkspaceStore.Parse(await File.ReadAllTextAsync(file.Path));
            if (imported is null) { ShowToast("That file is not a valid workspace."); return; }

            var wanted = string.IsNullOrWhiteSpace(imported.Workspace) ? Path.GetFileNameWithoutExtension(file.Path) : imported.Workspace;
            var name = WorkspaceStore.UniqueName(wanted);
            WorkspaceStore.Save(name, imported);
            ApplyWorkspace(name, imported);
            ShowToast(string.Format("Imported workspace “{0}”", name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast(string.Format("Could not import: {0}", ex.Message));
        }
    }

    // ---- Search ----

    private async void SearchGotFocus(object sender, RoutedEventArgs e) => await index.RefreshAsync(settings.Folder);

    private void SearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var hits = index.Search(sender.Text);
        sender.ItemsSource = hits.Count > 0 ? hits : new List<FileHit> { FileHit.None };
    }

    private void SearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var hit = args.ChosenSuggestion as FileHit ?? index.Search(args.QueryText, 1).FirstOrDefault();
        if (hit?.Path is null) { ShowToast("No matching scans."); return; }

        var reveal = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        try
        {
            if (reveal) Process.Start("explorer.exe", $"/select,\"{hit.Path}\"");
            else Process.Start(new ProcessStartInfo(hit.Path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            ShowToast(string.Format("Could not open: {0}", ex.Message));
        }
    }

    // ---- Scanning and importing ----

    private async void ScanClick(object sender, RoutedEventArgs e) => await StartScanAsync();

    private CancellationTokenSource? scanCancel;

    private void CancelScanClick(object sender, RoutedEventArgs e) => CancelScan();

    private bool CancelScan()
    {
        if (scanCancel is null || scanCancel.IsCancellationRequested) return false;
        scanCancel.Cancel();
        CancelScanButton.IsEnabled = false;
        BusyStatus.Text = "Cancelling…"; // the page being transferred finishes first
        return true;
    }

    private async Task StartScanAsync(bool forceFast = false)
    {
        if (busy) return;
        ExitTools();
        SetBusy(true, "Scanning…");
        scanCancel = new CancellationTokenSource();
        var cancel = scanCancel.Token;
        CancelScanButton.IsEnabled = true;
        CancelScanButton.Visibility = Visibility.Visible;
        try
        {
            var fast = settings.Fast || forceFast;
            var dpi = fast ? Math.Min(settings.Dpi, FastDpiCap) : settings.Dpi;
            var options = new ScanOptions(settings.Source, settings.Color, dpi, settings.DoubleSided, fast,
                settings.ScannerId, settings.Paper, settings.UseTwain);
            var progress = new Progress<int>(n => BusyStatus.Text = string.Format("Scanning page {0}…", n));

            var result = await ScannerService.ScanAsync(options, progress, cancel);
            var raw = result.Pages;

            // No duplex hardware: scan the fronts, ask the user to flip the stack, then scan the backs.
            if (settings.DoubleSided && !result.HardwareDuplex)
            {
                CancelScanButton.Visibility = Visibility.Collapsed;
                if (await AskFlipAsync(raw.Count))
                {
                    CancelScanButton.IsEnabled = true;
                    CancelScanButton.Visibility = Visibility.Visible;
                    var backProgress = new Progress<int>(n => BusyStatus.Text = string.Format("Scanning back side {0}…", n));
                    var backs = await ScannerService.ScanAsync(options, backProgress, cancel);
                    raw = Interleave(raw, backs.Pages);
                }
            }

            CancelScanButton.Visibility = Visibility.Collapsed; // scanning is over; only processing is left
            var processing = Stopwatch.StartNew();
            await AddPagesAsync(raw, fast);
            Log.Info($"App processing of {raw.Count} page(s) took {processing.ElapsedMilliseconds} ms");
            if (!result.PaperSizeApplied)
                ShowToast(string.Format("This scanner's driver didn't accept the {0} page size, so its own size was used. Running Every Scanner as administrator may allow it.", settings.Paper));
        }
        catch (OperationCanceledException ex)
        {
            Log.Info("Scan cancelled by the user.");
            ShowToast(ScannerService.Describe(ex));
        }
        catch (Exception ex)
        {
            Log.Error("Scan", ex);
            ShowToast(ScannerService.Describe(ex), Log.CurrentFile, "Open log", openFile: true);
        }
        finally
        {
            CancelScanButton.Visibility = Visibility.Collapsed;
            scanCancel?.Dispose();
            scanCancel = null;
            SetBusy(false);
        }
    }

    /// <summary>Merges fronts with backs scanned from the flipped stack (so backs arrive last-sheet-first).</summary>
    private List<byte[]> Interleave(List<byte[]> fronts, List<byte[]> backsFlipped)
    {
        var backs = Enumerable.Reverse(backsFlipped).ToList();
        if (backs.Count != fronts.Count)
            ShowToast(string.Format("Front and back counts differ ({0} vs {1}); pages may be out of order.", fronts.Count, backs.Count));

        var merged = new List<byte[]>();
        for (var i = 0; i < fronts.Count; i++)
        {
            merged.Add(fronts[i]);
            if (i < backs.Count) merged.Add(backs[i]);
        }
        merged.AddRange(backs.Skip(fronts.Count));
        return merged;
    }

    private async Task<bool> AskFlipAsync(int frontCount)
    {
        var dialog = new ContentDialog
        {
            Title = "Scan the back sides",
            PrimaryButtonText = "Continue",
            CloseButtonText = "Skip backs",
            DefaultButton = ContentDialogButton.Primary,
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Text = string.Format("{0} front side(s) scanned. This scanner can't scan both sides at once, so turn the whole stack over (don't reorder it), load it into the feeder and press Continue.", frontCount),
            },
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private async void ImportClick(object sender, RoutedEventArgs e) => await ImportAsync();

    private async Task ImportAsync()
    {
        if (busy) return;
        ExitTools();
        var picker = new FileOpenPicker();
        foreach (var ext in new[] { ".pdf", ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" })
            picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;

        SetBusy(true, "Importing…");
        try
        {
            var raw = new List<byte[]>();
            foreach (var file in files)
            {
                if (string.Equals(Path.GetExtension(file.Path), ".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var progress = new Progress<(int Done, int Total)>(p =>
                        BusyStatus.Text = string.Format("Reading {0}: page {1} of {2}…", file.Name, p.Done, p.Total));
                    raw.AddRange(await PdfImporter.RenderAsync(file.Path, progress));
                }
                else
                    raw.Add(await File.ReadAllBytesAsync(file.Path));
            }
            await AddPagesAsync(raw, fast: false);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task AddPagesAsync(List<byte[]> raw, bool fast)
    {
        var (skipBlank, straighten) = (settings.SkipBlank, settings.AutoStraighten);
        BusyStatus.Text = string.Format("Processing {0} page(s)…", raw.Count);

        // Pages are independent: prepare them in parallel, then render previews in parallel too.
        var prepared = await Task.WhenAll(raw.Select(r => Task.Run(() => ImageEnhancer.Prepare(r, skipBlank, fast, straighten))));
        var added = prepared.Where(p => p is not null).Select(p => new ScanPage(p!)).ToList();
        foreach (var page in added) page.Rendered += WarmPdf;
        var skipped = prepared.Length - added.Count;

        if (settings.SmartMode && !fast)
        {
            var done = 0;
            BusyStatus.Text = string.Format("Smart mode: checking page {0} of {1}…", 1, added.Count);
            await Task.WhenAll(added.Select(async page =>
            {
                var look = await Task.Run(() => ImageEnhancer.FindSmartLook(page.Raw));
                if (look is { } l) await page.SetLookAsync(l.Mode, l.Brightness, l.Contrast, l.Highlights, l.Shadows);
                else await page.RenderAsync();
                BusyStatus.Text = string.Format("Smart mode: checked {0} of {1} pages…", Interlocked.Increment(ref done), added.Count);
            }));
        }
        else
            await Task.WhenAll(added.Select(p => p.RenderAsync()));
        foreach (var page in added)
        {
            page.SelectionChanged += UpdateUi;
            pages.Add(page);
        }

        if (added.Count > 0) SetCurrent(added[0]);
        if (skipped > 0) ShowToast(string.Format("Skipped {0} blank page(s).", skipped));
    }

    /// <summary>Gets a page's PDF bytes ready in the background once it has stopped changing, so Save is quick.</summary>
    private async void WarmPdf(ScanPage page)
    {
        if (settings.Format != OutputFormat.Pdf) return;
        var source = page.Processed;
        await Task.Delay(600); // sliders re-render constantly; wait for the page to settle
        if (ReferenceEquals(source, page.Processed)) _ = page.PdfBytes(settings.Quality);
    }

    // ---- Page editing ----

    private void FilmstripItemClick(object sender, ItemClickEventArgs e) => SetCurrent((ScanPage)e.ClickedItem);

    private void SetCurrent(ScanPage? page)
    {
        EndCompare();
        ExitTools();
        if (current is not null) current.IsCurrent = false;
        current = page;
        if (page is not null) page.IsCurrent = true;
        PreviewImage.Source = page?.Preview;
        ResetZoom();
        if (page is not null) Filmstrip.ScrollIntoView(page);
        SyncSliders();
        UpdateUi();
    }

    /// <summary>Jumps to page <paramref name="index"/> (0-based), clamped to the available pages.</summary>
    private void GoTo(int target)
    {
        if (pages.Count == 0) return;
        SetCurrent(pages[Math.Clamp(target, 0, pages.Count - 1)]);
    }

    /// <summary>Moves the current page earlier (-1) or later (+1) in the batch.</summary>
    private void MovePage(int delta)
    {
        if (current is null || busy) return;
        var from = pages.IndexOf(current);
        var to = from + delta;
        if (to < 0 || to >= pages.Count) return;
        pages.Move(from, to);
        Filmstrip.ScrollIntoView(current);
        UpdateUi();
    }

    private void Step(int delta) => GoTo((current is null ? 0 : pages.IndexOf(current)) + delta);

    private async void ModeClick(object sender, RoutedEventArgs e)
    {
        if (current is null || busy) return;
        var mode = Enum.Parse<EnhanceMode>((string)((Button)sender).Tag);
        await RunOnCurrentAsync(page => page.SetModeAsync(mode));
    }

    private async void ApplyAllClick(object sender, RoutedEventArgs e)
    {
        if (current is null || busy) return;
        var (mode, brightness, contrast, highlights, shadows) = (current.Mode, current.Brightness, current.Contrast, current.Highlights, current.Shadows);
        SetBusy(true, "Applying to all pages…");
        try
        {
            await Task.WhenAll(pages.Select(p => p.SetLookAsync(mode, brightness, contrast, highlights, shadows)));
            PreviewImage.Source = current.Preview;
        }
        finally { SetBusy(false); }
    }

    private async Task RunOnCurrentAsync(Func<ScanPage, Task> action)
    {
        if (current is null || busy) return;
        var page = current;
        SetBusy(true, "Working…");
        try
        {
            await action(page);
            if (page == current) PreviewImage.Source = page.Preview;
        }
        finally { SetBusy(false); }
    }

    private async void RotateLeftClick(object sender, RoutedEventArgs e) => await RotateAsync(false);

    private async void RotateRightClick(object sender, RoutedEventArgs e) => await RotateAsync(true);

    private async Task RotateAsync(bool clockwise)
    {
        ExitTools();
        await RunOnCurrentAsync(page => page.RotateAsync(clockwise));
        ResetZoom(); // the page's shape changed
    }

    private void DeleteClick(object sender, RoutedEventArgs e) => DeleteCurrent();

    private void DeleteCurrent()
    {
        if (current is null) return;
        var i = pages.IndexOf(current);
        pages.Remove(current);
        SetCurrent(pages.Count == 0 ? null : pages[Math.Min(i, pages.Count - 1)]);
    }

    private void SelectAllClick(object sender, RoutedEventArgs e) => ToggleSelectAll();

    private void ToggleSelectAll()
    {
        var selectAll = pages.Any(p => !p.IsSelected);
        foreach (var page in pages) page.IsSelected = selectAll;
    }

    private async void ClearClick(object sender, RoutedEventArgs e) => await ClearAllAsync();

    private async Task ClearAllAsync()
    {
        if (pages.Count == 0 || busy) return;
        var dialog = new ContentDialog
        {
            Title = "Clear all scans?",
            PrimaryButtonText = "Clear all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Text = string.Format("This removes all {0} page(s) from the batch. Pages you haven't saved will be lost.", pages.Count),
            },
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        pages.Clear();
        SetCurrent(null);
        ShowToast("Cleared all scans.");
    }

    private void OnPagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        for (var i = 0; i < pages.Count; i++) pages[i].Number = i + 1;
        QueueRecord();
        UpdateUi();
    }

    // ---- Zoom (Ctrl+wheel is built into the ScrollViewer; 1.0 = fit to window) ----

    private const double MaxZoom = 8;
    private const double ZoomStep = 1.25;

    /// <summary>Keeps the zoom content exactly the size of the window so that zoom 1.0 means "fit".</summary>
    private void PreviewScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ZoomContent.Width = e.NewSize.Width;
        ZoomContent.Height = e.NewSize.Height;
    }

    private void PreviewViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        var zoom = PreviewScroll.ZoomFactor;
        ZoomText.Content = Math.Abs(zoom - 1) < 0.005 ? "Fit" : $"{zoom * 100:0}%";
        RotateGrips.GripScale = 1 / zoom; // grips stay the same size on screen
        QuadArea.HandleScale = 1 / zoom;
    }

    /// <summary>Zooms towards the centre of what's currently visible.</summary>
    private void ZoomBy(double factor)
    {
        if (pages.Count == 0) return;
        var old = PreviewScroll.ZoomFactor;
        var zoom = Math.Clamp(old * factor, 1, MaxZoom);
        var ratio = zoom / old;
        var x = (PreviewScroll.HorizontalOffset + PreviewScroll.ViewportWidth / 2) * ratio - PreviewScroll.ViewportWidth / 2;
        var y = (PreviewScroll.VerticalOffset + PreviewScroll.ViewportHeight / 2) * ratio - PreviewScroll.ViewportHeight / 2;
        PreviewScroll.ChangeView(Math.Max(0, x), Math.Max(0, y), (float)zoom);
    }

    private void ResetZoom() => PreviewScroll.ChangeView(0, 0, 1f, disableAnimation: true);

    // ---- Pan: hold Space and drag the page ----

    private bool panning;
    private Windows.Foundation.Point panStart;
    private (double X, double Y) panOrigin;

    private static bool SpaceHeld => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Space).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Space is the pan modifier, so it must not also press the focused button or tick a filmstrip tile.</summary>
    private void RootPreviewSpace(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Space && !dialogOpen && !IsTyping() && pages.Count > 0) e.Handled = true;
    }

    /// <summary>
    /// While cropping, Enter applies and Esc cancels no matter what has focus (a slider, a drop-down or a page tile
    /// would otherwise swallow the key until the user clicked the crop bar).
    /// </summary>
    private void RootPreviewCropKey(object sender, KeyRoutedEventArgs e)
    {
        if (!(cropping || quadding) || dialogOpen || e.Key is not (VirtualKey.Enter or VirtualKey.Escape)) return;
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or ComboBox or ComboBoxItem) return;
        if (e.Key == VirtualKey.Enter) _ = cropping ? ApplyCropAsync() : ApplyQuadAsync();
        else ExitTools();
        e.Handled = true;
    }

    private void PanPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!SpaceHeld || pages.Count == 0) return;
        panning = true;
        panStart = e.GetCurrentPoint(PreviewScroll).Position;
        panOrigin = (PreviewScroll.HorizontalOffset, PreviewScroll.VerticalOffset);
        PreviewScroll.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PanMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!panning) return;
        var p = e.GetCurrentPoint(PreviewScroll).Position;
        PreviewScroll.ChangeView(panOrigin.X - (p.X - panStart.X), panOrigin.Y - (p.Y - panStart.Y), null, disableAnimation: true);
        e.Handled = true;
    }

    private void PanEnded(object sender, PointerRoutedEventArgs e)
    {
        if (!panning) return;
        panning = false;
        PreviewScroll.ReleasePointerCapture(e.Pointer);
    }

    private void ZoomInClick(object sender, RoutedEventArgs e) => ZoomBy(ZoomStep);

    private void ZoomOutClick(object sender, RoutedEventArgs e) => ZoomBy(1 / ZoomStep);

    private void ZoomFitClick(object sender, RoutedEventArgs e) => ResetZoom();

    // ---- Brightness and contrast (per page) ----

    private double rotateStart;

    private void SyncSliders()
    {
        syncingSliders = true;
        BrightnessSlider.Value = current?.Brightness ?? 0;
        ContrastSlider.Value = current?.Contrast ?? 0;
        HighlightsSlider.Value = current?.Highlights ?? 0;
        ShadowsSlider.Value = current?.Shadows ?? 0;
        StraightenSlider.Value = current?.Angle ?? 0;
        syncingSliders = false;
        UpdateAdjustLabels();
    }

    private void UpdateAdjustLabels()
    {
        BrightnessValue.Text = ((int)BrightnessSlider.Value).ToString("+0;-0;0");
        ContrastValue.Text = ((int)ContrastSlider.Value).ToString("+0;-0;0");
        HighlightsValue.Text = ((int)HighlightsSlider.Value).ToString("+0;-0;0");
        ShadowsValue.Text = ((int)ShadowsSlider.Value).ToString("+0;-0;0");
        StraightenValue.Text = StraightenSlider.Value.ToString("+0.0;-0.0;0.0") + "°";
    }

    private async void AutoStraightenClick(object sender, RoutedEventArgs e) => await AutoStraightenCurrentAsync();

    private async Task AutoStraightenCurrentAsync()
    {
        if (current is null || busy) return;
        ExitTools();
        await RunOnCurrentAsync(page => page.AutoStraightenAsync());
        ResetZoom();
        SyncSliders();
        ShowToast("Page straightened. Use the Straighten slider to fine-tune, or Reset edits to undo.");
    }

    private async void AutoStraightenAllClick(object sender, RoutedEventArgs e)
    {
        if (pages.Count == 0 || busy) return;
        ExitTools();
        SetBusy(true, "Straightening all pages…");
        try
        {
            await Task.WhenAll(pages.Select(p => p.AutoStraightenAsync()));
            if (current is not null) PreviewImage.Source = current.Preview;
            ResetZoom();
            SyncSliders();
            ShowToast(string.Format("Straightened {0} page(s).", pages.Count));
        }
        finally { SetBusy(false); }
    }

    private async void AdjustChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (syncingSliders || current is null) return;
        var page = current;
        page.SetAdjustments((int)BrightnessSlider.Value, (int)ContrastSlider.Value, (int)HighlightsSlider.Value, (int)ShadowsSlider.Value);
        page.SetAngle(Math.Round(StraightenSlider.Value, 1));
        UpdateAdjustLabels();

        var version = ++adjustVersion;
        await Task.Delay(120); // wait for the slider to settle before re-rendering
        if (version != adjustVersion) return;
        await page.RenderAsync();
        if (page == current) PreviewImage.Source = page.Preview;
        Record();
    }

    private async void ResetEditsClick(object sender, RoutedEventArgs e)
    {
        ExitTools();
        await RunOnCurrentAsync(page => page.ResetEditsAsync());
        ResetZoom();
        SyncSliders();
    }

    // ---- Crop (always acts on the current page only) ----

    private void CropClick(object sender, RoutedEventArgs e)
    {
        if (cropping) ExitCrop(); else EnterCrop();
    }

    /// <summary>Leaves whichever on-page tool (crop or dodge/burn) is active.</summary>
    private void ExitTools()
    {
        ExitCrop();
        ExitBrush();
        ExitQuad();
    }

    private void EnterCrop()
    {
        if (current is null || busy || cropping) return;
        ExitBrush();
        ExitQuad();
        cropping = true;
        CropArea.PagePixels = (current.PixelWidth, current.PixelHeight);
        CropArea.Reset(); // no selection yet: the user draws one with a click-and-drag
        CropArea.Visibility = Visibility.Visible;
        CropBar.Visibility = Visibility.Visible;
        UpdateUi();
    }

    private void OnCropSelectionChanged()
    {
        CropApplyButton.IsEnabled = CropArea.HasSelection;
        CropHint.Text = CropArea.HasSelection
            ? "Drag to move or resize · Enter or double-click to apply"
            : "Click and drag on the page to select the area to keep";
    }

    private void CropAllClick(object sender, RoutedEventArgs e) => CropArea.SelectAll();

    private void ExitCrop()
    {
        if (!cropping) return;
        cropping = false;
        CropArea.Visibility = Visibility.Collapsed;
        CropBar.Visibility = Visibility.Collapsed;
        UpdateUi();
    }

    private void CropResetClick(object sender, RoutedEventArgs e) => CropArea.Reset();

    private void CropCancelClick(object sender, RoutedEventArgs e) => ExitCrop();

    private async void CropApplyClick(object sender, RoutedEventArgs e) => await ApplyCropAsync();

    private async Task ApplyCropAsync()
    {
        if (!cropping) return;
        if (!CropArea.HasSelection)
        {
            ShowToast("Click and drag on the page to select the area first.");
            return;
        }
        var area = CropArea.Normalized;
        ExitTools();
        await RunOnCurrentAsync(page => page.CropAsync(area.X, area.Y, area.W, area.H));
        ResetZoom();
    }

    // ---- Compare: hold \ (or the eye button) to see the page as first scanned, release to get the edit back ----

    private const VirtualKey CompareKey = (VirtualKey)220; // the backslash key

    private bool comparing;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScanPage, Microsoft.UI.Xaml.Media.Imaging.BitmapImage> originalPreviews = new();

    private void RootPreviewCompareDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != CompareKey || dialogOpen || IsTyping()) return;
        e.Handled = true; // key repeat keeps arriving while it is held; BeginCompareAsync ignores it
        _ = BeginCompareAsync();
    }

    private void RootPreviewCompareUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == CompareKey) EndCompare();
    }

    private async Task BeginCompareAsync()
    {
        var page = current;
        if (comparing || page is null || busy || cropping) return;
        comparing = true;
        try
        {
            if (!originalPreviews.TryGetValue(page, out var bitmap))
            {
                bitmap = await ImageHelper.ToBitmapAsync(page.Original);
                originalPreviews.AddOrUpdate(page, bitmap);
            }
            if (!comparing || page != current) return; // let go (or changed page) while it was loading
            PreviewImage.Source = bitmap;
            CompareBadge.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            comparing = false;
            Log.Error("Compare with original", ex);
        }
    }

    private void EndCompare()
    {
        if (!comparing) return;
        comparing = false;
        CompareBadge.Visibility = Visibility.Collapsed;
        PreviewImage.Source = current?.Preview;
    }

    // ---- Split a page in two (a book scanned open) ----

    private async void SplitClick(object sender, RoutedEventArgs e) => await SplitCurrentAsync();

    /// <summary>Shows where the cut would fall on the preview: <paramref name="percent"/> of the way across (or down).</summary>
    private void ShowSplitLine(bool sideBySide, double percent)
    {
        var across = PreviewImage.ActualWidth * percent / 100;
        var down = PreviewImage.ActualHeight * percent / 100;
        SplitLine.HorizontalAlignment = sideBySide ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        SplitLine.VerticalAlignment = sideBySide ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        SplitLine.Width = sideBySide ? 2 : double.NaN;
        SplitLine.Height = sideBySide ? double.NaN : 2;
        SplitLine.Margin = sideBySide ? new Thickness(across - 1, 0, 0, 0) : new Thickness(0, down - 1, 0, 0);
        SplitLine.Visibility = Visibility.Visible;
    }

    private async Task SplitCurrentAsync()
    {
        var page = current;
        if (page is null || busy) return;
        ExitTools();

        var sideBySide = page.PixelWidth >= page.PixelHeight; // a landscape page is almost always two pages next to each other
        var direction = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 13, SelectedIndex = sideBySide ? 0 : 1 };
        direction.Items.Add("Side by side: cut into a left and a right page");
        direction.Items.Add("Stacked: cut into a top and a bottom page");
        var order = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 13, SelectedIndex = 0 };
        order.Items.Add("Left (top) page first");
        order.Items.Add("Right (bottom) page first: Persian and Arabic books");
        var position = new Slider { Minimum = 20, Maximum = 80, Value = 50, StepFrequency = 0.5 };
        var positionText = new TextBlock { Style = (Style)Application.Current.Resources["MutedText"] };

        void Refresh()
        {
            positionText.Text = $"Cut at {position.Value:0.#}% (the orange line on the page)";
            ShowSplitLine(direction.SelectedIndex == 0, position.Value);
        }
        direction.SelectionChanged += (_, _) => Refresh();
        position.ValueChanged += (_, _) => Refresh();
        Refresh();

        var content = new StackPanel { Spacing = 10, MinWidth = 360 };
        content.Children.Add(direction);
        content.Children.Add(positionText);
        content.Children.Add(position);
        content.Children.Add(order);

        var dialog = new ContentDialog
        {
            Title = "Split page",
            PrimaryButtonText = "Split",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
        };
        var accepted = await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
        SplitLine.Visibility = Visibility.Collapsed;
        if (!accepted || page != current) return;

        var (side, cutAt, flip) = (direction.SelectedIndex == 0, position.Value / 100, order.SelectedIndex == 1);
        SetBusy(true, "Splitting…");
        try
        {
            var baked = await page.BakedAsync();
            var (a, b) = await Task.Run(() => ImageEnhancer.Split(baked, side, cutAt));
            var halves = flip ? new[] { b, a } : new[] { a, b };

            var made = new List<ScanPage>();
            foreach (var bytes in halves)
            {
                var half = new ScanPage(bytes, page.Mode) { IsSelected = page.IsSelected };
                half.SetAdjustments(page.Brightness, page.Contrast, page.Highlights, page.Shadows);
                half.Rendered += WarmPdf;
                half.SelectionChanged += UpdateUi;
                made.Add(half);
            }
            await Task.WhenAll(made.Select(h => h.RenderAsync()));

            var at = pages.IndexOf(page);
            pages.RemoveAt(at);
            pages.Insert(at, made[0]);
            pages.Insert(at + 1, made[1]);
            SetCurrent(made[0]);
            ShowToast("Page split in two. Ctrl+Z undoes it.");
        }
        catch (Exception ex)
        {
            Log.Error("Split page", ex);
            ShowToast("Could not split this page.");
        }
        finally { SetBusy(false); }
    }

    // ---- Page corners (flattens the page to four corners the user places) ----

    private void QuadClick(object sender, RoutedEventArgs e)
    {
        if (quadding) ExitQuad(); else _ = EnterQuadAsync();
    }

    private async Task EnterQuadAsync()
    {
        var page = current;
        if (page is null || busy || quadding) return;
        ExitCrop();
        ExitBrush();
        quadding = true;
        QuadArea.SelectAll();
        QuadArea.Visibility = Visibility.Visible;
        QuadBar.Visibility = Visibility.Visible;
        UpdateUi();
        await DetectQuadAsync(page, announce: false);
    }

    private async Task DetectQuadAsync(ScanPage page, bool announce)
    {
        QuadHint.Text = "Looking for the page edges…";
        try
        {
            var found = await page.DetectCornersAsync();
            if (!quadding || page != current) return; // closed or changed page while looking
            if (found is not null) QuadArea.SetCorners(found);
            QuadHint.Text = found is not null
                ? "Drag the corners to fix the edges, then press Enter"
                : "No clear page edge found. Drag the four corners onto the page corners";
            if (found is null && announce) ShowToast("No clear page edge found.");
        }
        catch (Exception ex)
        {
            Log.Error("Find page corners", ex);
            QuadHint.Text = "Drag the four corners onto the corners of the page";
        }
    }

    private void ExitQuad()
    {
        if (!quadding) return;
        quadding = false;
        QuadArea.Visibility = Visibility.Collapsed;
        QuadBar.Visibility = Visibility.Collapsed;
        UpdateUi();
    }

    private void QuadDetectClick(object sender, RoutedEventArgs e)
    {
        if (current is not null) _ = DetectQuadAsync(current, announce: true);
    }

    private void QuadAllClick(object sender, RoutedEventArgs e) => QuadArea.SelectAll();

    private void QuadCancelClick(object sender, RoutedEventArgs e) => ExitQuad();

    private async void QuadApplyClick(object sender, RoutedEventArgs e) => await ApplyQuadAsync();

    private async Task ApplyQuadAsync()
    {
        if (!quadding) return;
        var corners = QuadArea.Corners.ToArray();
        ExitQuad();
        await RunOnCurrentAsync(page => page.FlattenToCornersAsync(corners));
        ResetZoom();
        SyncSliders();
    }

    // ---- Dodge and burn (paints into the current page) ----

    private void BrushClick(object sender, RoutedEventArgs e)
    {
        if (painting) ExitBrush(); else EnterBrush();
    }

    private void EnterBrush()
    {
        if (current is null || busy || painting) return;
        ExitCrop();
        ExitQuad();
        painting = true;
        ApplyBrushSettings();
        BrushArea.Visibility = Visibility.Visible;
        BrushBar.Visibility = Visibility.Visible;
        UpdateUi();
    }

    private void ExitBrush()
    {
        if (!painting) return;
        painting = false;
        BrushArea.Visibility = Visibility.Collapsed;
        BrushBar.Visibility = Visibility.Collapsed;
        UpdateUi();
    }

    private void BrushDoneClick(object sender, RoutedEventArgs e) => ExitBrush();

    private void DodgeClick(object sender, RoutedEventArgs e) => SetBrushKind(ImageEnhancer.BrushKind.Dodge);

    private void BurnClick(object sender, RoutedEventArgs e) => SetBrushKind(ImageEnhancer.BrushKind.Burn);

    private void EraseClick(object sender, RoutedEventArgs e) => SetBrushKind(ImageEnhancer.BrushKind.Erase);

    private void SetBrushKind(ImageEnhancer.BrushKind kind)
    {
        if (kind == brushKind) return;
        var toErase = kind == ImageEnhancer.BrushKind.Erase;
        var fromErase = brushKind == ImageEnhancer.BrushKind.Erase;
        if (toErase != fromErase)
        {
            // Swap in the strength last used for the other group of tools.
            if (toErase) toneExposure = BrushExposureSlider.Value; else eraseExposure = BrushExposureSlider.Value;
            brushKind = kind;
            BrushExposureSlider.Value = toErase ? eraseExposure : toneExposure;
        }
        brushKind = kind;
        ApplyBrushSettings();
    }

    private void BrushSettingChanged(object sender, RoutedEventArgs e) => ApplyBrushSettings();

    private void ApplyBrushSettings()
    {
        if (BrushArea is null || BrushExposureSlider is null) return; // load-time events arrive before everything exists
        BrushArea.Diameter = BrushSizeSlider.Value / 100;
        BrushArea.TrailColor = brushKind switch
        {
            ImageEnhancer.BrushKind.Burn => Colors.Black,
            ImageEnhancer.BrushKind.Erase => Colors.Orange,
            _ => Colors.White,
        };
        Mark(DodgeButton, brushKind == ImageEnhancer.BrushKind.Dodge);
        Mark(BurnButton, brushKind == ImageEnhancer.BrushKind.Burn);
        Mark(EraseButton, brushKind == ImageEnhancer.BrushKind.Erase);
        RangeCombo.IsEnabled = brushKind != ImageEnhancer.BrushKind.Erase; // erasing doesn't depend on tone
    }

    private ImageEnhancer.ToneRange SelectedRange() => RangeCombo.SelectedIndex switch
    {
        0 => ImageEnhancer.ToneRange.Shadows,
        2 => ImageEnhancer.ToneRange.Highlights,
        _ => ImageEnhancer.ToneRange.Midtones,
    };

    private async void OnBrushStroke(IReadOnlyList<(double X, double Y)> points)
    {
        var page = current;
        if (page is null)
        {
            BrushArea.ClearTrails();
            return;
        }
        brushPending++;
        var stroke = new ImageEnhancer.BrushStroke(points, BrushSizeSlider.Value / 100, brushKind, SelectedRange(), (int)BrushExposureSlider.Value);
        try
        {
            await page.DodgeBurnAsync(stroke);
            if (page == current)
            {
                PreviewImage.Source = page.Preview;
                SyncSliders(); // painting bakes the fine-straighten angle into the page
            }
        }
        catch (Exception ex)
        {
            Log.Error("Dodge/burn", ex);
            ShowToast("Could not paint on this page.");
        }
        finally
        {
            if (--brushPending == 0) BrushArea.ClearTrails(); // the preview now shows the result
            Record();
        }
    }

    // ---- Undo and redo (Ctrl+Z / Ctrl+Y): every edit, and adding, removing or moving pages ----

    private const int MaxHistory = 40;

    /// <summary>The pages in order, each with its edits. Images are immutable, so a snapshot only holds references.</summary>
    private sealed record Snapshot((ScanPage Page, ScanPage.State State)[] Pages, ScanPage? Current);

    private readonly List<Snapshot> undoStack = [];
    private readonly List<Snapshot> redoStack = [];
    private Snapshot committed = new([], null);
    private bool restoring;
    private bool recordQueued;

    private Snapshot Capture() => new(pages.Select(p => (p, p.Save())).ToArray(), current);

    private static bool SameEdits(Snapshot a, Snapshot b) =>
        a.Pages.Length == b.Pages.Length
        && a.Pages.Zip(b.Pages).All(t => ReferenceEquals(t.First.Page, t.Second.Page) && t.First.State.Equals(t.Second.State));

    /// <summary>Stores the state before the latest change as an undo step, if anything changed since the last call.</summary>
    private void Record()
    {
        if (restoring) return;
        var now = Capture();
        if (!SameEdits(now, committed))
        {
            undoStack.Add(committed);
            if (undoStack.Count > MaxHistory) undoStack.RemoveAt(0);
            redoStack.Clear();
        }
        committed = now;
    }

    /// <summary>Page list changes can come in bursts (a scan adds many pages); record once for the whole burst.</summary>
    private void QueueRecord()
    {
        if (recordQueued) return;
        recordQueued = true;
        DispatcherQueue.TryEnqueue(() => { recordQueued = false; Record(); });
    }

    private Task UndoAsync() => TravelAsync(undoStack, redoStack);

    private Task RedoAsync() => TravelAsync(redoStack, undoStack);

    private async Task TravelAsync(List<Snapshot> from, List<Snapshot> to)
    {
        if (busy || from.Count == 0) return;
        adjustVersion++; // a slider change that hasn't rendered yet is replaced by the restored state
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(committed);

        restoring = true;
        SetBusy(true, "Working…");
        try
        {
            ExitTools();
            if (!pages.SequenceEqual(target.Pages.Select(p => p.Page)))
            {
                pages.Clear();
                foreach (var (page, _) in target.Pages) pages.Add(page);
            }
            await Task.WhenAll(target.Pages.Where(t => t.Page.Restore(t.State)).Select(t => t.Page.RenderAsync()));
            SetCurrent(target.Current is { } c && pages.Contains(c) ? c : pages.FirstOrDefault());
        }
        finally
        {
            committed = Capture();
            restoring = false;
            SetBusy(false);
        }
    }

    /// <summary>Ctrl+Z / Ctrl+Y belong to a text box while it has focus; everywhere else they act on the pages.</summary>
    private bool HistoryKey(Func<Task> action)
    {
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or PasswordBox) return false;
        _ = action();
        return true;
    }

    // ---- Saving ----

    private async void SaveClick(object sender, RoutedEventArgs e) => await SaveAsync();

    private async void AppendClick(object sender, RoutedEventArgs e) => await AppendAsync();

    /// <summary>Asks for a PDF saved earlier and adds the selected pages to its end.</summary>
    private async Task AppendAsync()
    {
        if (busy || !pages.Any(p => p.IsSelected)) return;
        ExitTools();
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        await SaveAsync(appendTo: file.Path);
    }

    private async Task SaveAsync(string? appendTo = null)
    {
        var chosen = pages.Where(p => p.IsSelected).ToList();
        if (chosen.Count == 0 || busy) return;

        var target = settings.TargetFolder(DateTime.Now);
        var kind = settings.Format switch { OutputFormat.Pdf => "a PDF", OutputFormat.Jpeg => "JPEG images", _ => "PNG images" };
        var name = appendTo is not null
            ? Path.GetFileName(appendTo)
            : await AskTextAsync(
                "Name your scan",
                "File name",
                "Save",
                string.Format("Scan {0:yyyy-MM-dd HH-mm}", DateTime.Now),
                string.Format("{0} page(s) will be saved as {1} in", chosen.Count, kind),
                target);
        if (name is null) return;

        SetBusy(true, "Saving…");
        try
        {
            var data = chosen.Select(p => p.Processed).ToList();
            var (format, quality) = (appendTo is not null ? OutputFormat.Pdf : settings.Format, settings.Quality);
            var folder = settings.TargetFolder(DateTime.Now); // recomputed: the day may have rolled over
            string path;
            var ocrNote = "";
            if (format == OutputFormat.Pdf)
            {
                // Usually already prepared in the background while the user was reviewing the pages.
                var ready = await Task.WhenAll(chosen.Select(p => p.PdfBytes(quality)));
                OcrService.Page?[]? text = null;
                if (settings.Ocr && OcrService.Available)
                {
                    BusyStatus.Text = "Reading the text…";
                    text = await Task.WhenAll(chosen.Select(p => p.Text()));
                    if (text.All(t => t is null)) ocrNote = " Text could not be read, so the PDF is not searchable.";
                    else if (!OcrService.PersianAvailable) ocrNote = " Only English text is searchable: add the Persian OCR language pack in Windows to read Persian.";
                }
                else if (settings.Ocr)
                    ocrNote = " It is not searchable: install the Persian, Arabic or English OCR language pack in Windows.";
                path = appendTo is not null
                    ? await Task.Run(() => PdfExporter.Append(appendTo, ready, text))
                    : await Task.Run(() => PdfExporter.Save(ready, folder, name, text));
            }
            else
            {
                path = await Task.Run(() => OutputService.Save(data, format, quality, folder, name));
            }

            foreach (var page in chosen) pages.Remove(page);
            SetCurrent(pages.Count == 0 ? null : pages[0]);
            DocumentTitle.Text = Path.GetFileName(path);
            var summary = format != OutputFormat.Pdf && data.Count > 1 ? string.Format("Saved {0} images to {1}", data.Count, Path.GetFileName(folder)) : string.Format(appendTo is not null ? "Added {0} page(s) to {1}" : "Saved {1}", data.Count, Path.GetFileName(path));
            ShowToast(summary + ocrNote, path);
            _ = index.RefreshAsync(settings.Folder);
        }
        catch (Exception ex)
        {
            Log.Error("Save", ex);
            ShowToast(string.Format("Could not save: {0}", ex.Message), Log.CurrentFile, "Open log", openFile: true);
        }
        finally { SetBusy(false); }
    }

    // ---- Dialogs ----

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (dialogOpen) return ContentDialogResult.None;
        dialogOpen = true;
        dialog.XamlRoot = Root.XamlRoot;
        dialog.RequestedTheme = Root.ActualTheme;
        try { return await dialog.ShowAsync(); }
        finally { dialogOpen = false; }
    }

    /// <summary>Shows a dialog with one text box; returns the entered text, or null if cancelled.</summary>
    private async Task<string?> AskTextAsync(string title, string placeholder, string okText, string initial, string? line1 = null, string? line2 = null)
    {
        var box = new TextBox { Text = initial, PlaceholderText = placeholder, FontSize = 13 };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };

        var content = new StackPanel { Spacing = 8 };
        if (line1 is not null) content.Children.Add(new TextBlock { Text = line1, Style = (Style)Application.Current.Resources["MutedText"] });
        if (line2 is not null) content.Children.Add(new TextBlock { Text = line2, TextTrimming = TextTrimming.CharacterEllipsis });
        content.Children.Add(box);

        var dialog = new ContentDialog
        {
            Title = title,
            PrimaryButtonText = okText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary ? box.Text : null;
    }

    private const string DeveloperName = "Arman Soltani";
    private const string DeveloperEmail = "imrealarman@gmail.com";

    private async void AboutClick(object sender, RoutedEventArgs e) => await ShowAboutAsync();

    private async Task ShowAboutAsync()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        var versionText = version is null ? "" : string.Format("Version {0}.{1}.{2}", version.Major, version.Minor, version.Build);
        var muted = (Style)Application.Current.Resources["MutedText"];
        var header = (Style)Application.Current.Resources["PanelHeader"];

        var logo = new Image { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Left };
        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
        if (File.Exists(logoPath)) logo.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(logoPath));

        var sendMail = new Button { Content = "Send email", Style = (Style)Application.Current.Resources["PrimaryButton"] };
        sendMail.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo($"mailto:{DeveloperEmail}?subject=Every Scanner") { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception) { ShowToast(string.Format("No email app is set up. Write to {0}.", DeveloperEmail)); }
        };
        var copyMail = new Button { Content = "Copy email", Style = (Style)Application.Current.Resources["SecondaryButton"] };
        copyMail.Click += (_, _) =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(DeveloperEmail);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            copyMail.Content = "Copied";
        };

        var openLog = new Button { Content = "Open log", Style = (Style)Application.Current.Resources["SecondaryButton"] };
        openLog.Click += (_, _) =>
        {
            try
            {
                var file = Log.CurrentFile;
                if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                else Process.Start("explorer.exe", $"\"{Log.Folder}\"");
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { ShowToast(string.Format("Could not open the log: {0}", ex.Message)); }
        };

        var content = new StackPanel { Spacing = 14, MinWidth = 380 };
        content.Children.Add(logo);
        content.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "Every Scanner", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock { Text = versionText, Style = muted },
            },
        });
        content.Children.Add(new TextBlock
        {
            Text = "Scan a whole stack of pages at once, clean them up, and save them as a PDF or images. Everything works offline on your PC.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        });
        content.Children.Add(new Border { Height = 1, Background = Res("BorderBrush1") });
        content.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "DEVELOPER", Style = header },
                new TextBlock { Text = DeveloperName, FontSize = 15 },
                new TextBlock { Text = DeveloperEmail, FontSize = 13, Foreground = Accent, IsTextSelectionEnabled = true },
            },
        });
        content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { sendMail, copyMail, openLog } });
        content.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "Built with WinUI 3, .NET 8, OpenCV and PDFsharp", Style = muted },
                new TextBlock { Text = $"© 2026 {DeveloperName}", Style = muted },
            },
        });

        await ShowDialogAsync(new ContentDialog
        {
            Title = "About",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            Content = content,
        });
    }

    private async void HelpClick(object sender, RoutedEventArgs e) => await ShowHelpAsync();

    private async Task ShowHelpAsync()
    {
        (string Keys, string Action)[] rows =
        [
            ("Ctrl+N  /  F5", "Scan"),
            ("Ctrl+Shift+N", "Fast scan (one time)"),
            ("Esc", "Cancel the scan in progress (or the crop)"),
            ("Ctrl+I", "Import images or PDFs"),
            ("Ctrl+S", "Save selected pages"),
            ("Ctrl+E", "Export all pages, or only the selected ones, as JPEG images (the pages stay open)"),
            ("Ctrl+F", "Search saved scans"),
            ("Home  /  End", "First / last page"),
            ("←  →   PgUp  PgDn", "Previous / next page"),
            ("Ctrl+1 … 9,  Ctrl+0", "Jump to page 1–9, page 10"),
            ("Drag a page below", "Reorder pages (or Ctrl+Shift+←  /  →)"),
            ("Ctrl+Scroll,  Ctrl+=  /  Ctrl+−", "Zoom in / out (the +/− buttons too)"),
            ("Hold Space + drag", "Move around a zoomed-in page (pan)"),
            ("Drag a round corner grip", "Rotate the page a little (±15°)"),
            ("0", "Zoom to fit"),
            ("S", "Auto-straighten this page"),
            ("C", "Crop this page: drag on the page, Enter or double-click to apply, Esc to cancel"),
            ("B", "Split this page in two (a book scanned open)"),
            ("I", "Place an image on this page: set its size, direction and look"),
            ("Ctrl+B", "Add a blank page (A0–A6, Letter, custom…), optionally with an image on it"),
            ("P", "Adjust page corners: drag the four corners onto the page, Enter to straighten, Esc to cancel"),
            ("D", "Dodge, burn and erase: paint to lighten, darken, or remove marks. X swaps dodge / burn, [ and ] change the size, Esc finishes"),
            ("Hold  \\", "Show the original page while held, your edited page when released (the eye button works the same)"),
            ("Ctrl+Shift+S", "Add the selected pages to the end of a PDF you saved earlier"),
            ("Ctrl+Z  /  Ctrl+Y", "Undo / redo any edit, or adding, deleting and moving pages"),
            ("Ctrl+R  /  Ctrl+Shift+R", "Rotate right / left"),
            ("Delete", "Delete this page"),
            ("Ctrl+Shift+A", "Select / deselect all pages"),
            ("Ctrl+Shift+Delete", "Clear all scans"),
            ("Ctrl+T", "Cycle theme"),
            ("F1", "This list"),
        ];

        var list = new StackPanel { Spacing = 6, MinWidth = 400 };
        foreach (var (keys, action) in rows)
        {
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = keys, FontSize = 12, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left });
            var description = new TextBlock { Text = action, FontSize = 12, Style = (Style)Application.Current.Resources["MutedText"] };
            Grid.SetColumn(description, 1);
            row.Children.Add(description);
            list.Children.Add(row);
        }

        await ShowDialogAsync(new ContentDialog
        {
            Title = "Keyboard shortcuts",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            Content = list,
        });
    }

    // ---- Keyboard shortcuts ----

    private void RegisterShortcuts()
    {
        const VirtualKeyModifiers None = VirtualKeyModifiers.None;
        const VirtualKeyModifiers Ctrl = VirtualKeyModifiers.Control;
        const VirtualKeyModifiers CtrlShift = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift;

        Key(VirtualKey.N, Ctrl, () => { _ = StartScanAsync(); });
        Key(VirtualKey.F5, None, () => { _ = StartScanAsync(); });
        Key(VirtualKey.N, CtrlShift, () => { _ = StartScanAsync(forceFast: true); });
        Key(VirtualKey.I, Ctrl, () => { _ = ImportAsync(); });
        Key(VirtualKey.S, Ctrl, () => { _ = SaveAsync(); });
        Key(VirtualKey.S, CtrlShift, () => { _ = AppendAsync(); });
        Key(VirtualKey.E, Ctrl, () => { _ = ExportJpegAsync(); });
        Key(VirtualKey.F, Ctrl, () => SearchBox.Focus(FocusState.Programmatic));
        Key(VirtualKey.F1, None, () => { _ = ShowHelpAsync(); });
        Key(VirtualKey.T, Ctrl, CycleTheme);

        Key(VirtualKey.R, Ctrl, () => { _ = RotateAsync(true); });
        Key(VirtualKey.R, CtrlShift, () => { _ = RotateAsync(false); });
        Key(VirtualKey.A, CtrlShift, ToggleSelectAll);
        Key(VirtualKey.Delete, CtrlShift, () => { _ = ClearAllAsync(); });

        // Keys that normally belong to text boxes, sliders and drop-downs only fire when none of those has focus.
        Key(VirtualKey.Delete, None, DeleteCurrent, plain: true);
        Key(VirtualKey.C, None, () => { if (cropping) ExitCrop(); else EnterCrop(); }, plain: true);
        Key(VirtualKey.B, None, () => { _ = SplitCurrentAsync(); }, plain: true);
        Key(VirtualKey.I, None, () => { _ = PlaceOnCurrentAsync(); }, plain: true);
        Key(VirtualKey.B, Ctrl, () => { _ = AddBlankPageAsync(); });
        Key(VirtualKey.P, None, () => { if (quadding) ExitQuad(); else _ = EnterQuadAsync(); }, plain: true);
        Key(VirtualKey.D, None, () => { if (painting) ExitBrush(); else EnterBrush(); }, plain: true);
        Key(VirtualKey.X, None, () => { if (!painting) return false; SetBrushKind(brushKind == ImageEnhancer.BrushKind.Dodge ? ImageEnhancer.BrushKind.Burn : ImageEnhancer.BrushKind.Dodge); return true; }, plain: true);
        const int OpenBracket = 219, CloseBracket = 221;
        Key((VirtualKey)OpenBracket, None, () => { if (!painting) return false; BrushSizeSlider.Value -= 1; return true; }, plain: true);
        Key((VirtualKey)CloseBracket, None, () => { if (!painting) return false; BrushSizeSlider.Value += 1; return true; }, plain: true);
        Key(VirtualKey.Z, Ctrl, () => HistoryKey(UndoAsync));
        Key(VirtualKey.Y, Ctrl, () => HistoryKey(RedoAsync));
        Key(VirtualKey.Z, CtrlShift, () => HistoryKey(RedoAsync));
        Key(VirtualKey.Home, None, () => GoTo(0), plain: true);
        Key(VirtualKey.End, None, () => GoTo(int.MaxValue), plain: true);
        Key(VirtualKey.Left, None, () => Step(-1), plain: true);
        Key(VirtualKey.PageUp, None, () => Step(-1), plain: true);
        Key(VirtualKey.Left, CtrlShift, () => MovePage(-1));
        Key(VirtualKey.Right, CtrlShift, () => MovePage(1));
        Key(VirtualKey.Right, None, () => Step(1), plain: true);
        Key(VirtualKey.PageDown, None, () => Step(1), plain: true);
        Key(VirtualKey.S, None, () => { _ = AutoStraightenCurrentAsync(); }, plain: true);
        Key(VirtualKey.Number0, None, ResetZoom, plain: true);
        Key(VirtualKey.NumberPad0, None, ResetZoom, plain: true);

        // Zoom: Ctrl+= / Ctrl+- (also with Shift, since "+" needs it on many layouts) and the numpad +/-.
        const int OemPlus = 187, OemMinus = 189, Add = 107, Subtract = 109;
        foreach (var mods in new[] { Ctrl, CtrlShift })
        {
            Key((VirtualKey)OemPlus, mods, () => ZoomBy(ZoomStep));
            Key((VirtualKey)Add, mods, () => ZoomBy(ZoomStep));
            Key((VirtualKey)OemMinus, mods, () => ZoomBy(1 / ZoomStep));
            Key((VirtualKey)Subtract, mods, () => ZoomBy(1 / ZoomStep));
        }

        Key(VirtualKey.Enter, None, () => { if (!cropping && !quadding) return false; _ = cropping ? ApplyCropAsync() : ApplyQuadAsync(); return true; }, plain: true);
        Key(VirtualKey.Escape, None, () =>
        {
            if (CancelScan()) return true;
            if (!cropping && !painting && !quadding) return false;
            ExitTools();
            return true;
        }, plain: true);

        // Ctrl+1..9 jump to that page; Ctrl+0 jumps to page 10.
        for (var n = 1; n <= 10; n++)
        {
            var page = n;
            var digit = page == 10 ? 0 : page;
            Key((VirtualKey)((int)VirtualKey.Number0 + digit), Ctrl, () => GoTo(page - 1));
            Key((VirtualKey)((int)VirtualKey.NumberPad0 + digit), Ctrl, () => GoTo(page - 1));
        }
    }

    private void Key(VirtualKey key, VirtualKeyModifiers modifiers, Action action, bool plain = false) =>
        Key(key, modifiers, () => { action(); return true; }, plain);

    /// <param name="action">Returns true if it handled the key (so the focused control doesn't also see it).</param>
    private void Key(VirtualKey key, VirtualKeyModifiers modifiers, Func<bool> action, bool plain = false)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) =>
        {
            if (dialogOpen || (plain && IsTyping())) return;
            if (action()) e.Handled = true;
        };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    private bool IsTyping() =>
        FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or ComboBox or ComboBoxItem or Slider or PasswordBox;

    // ---- UI state ----

    private void SetBusy(bool value, string status = "")
    {
        busy = value;
        Progress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        BusyStatus.Text = value ? status : "";
        if (!value) Record(); // the action that kept us busy is finished: store it as an undo step
        UpdateUi();
    }

    private void UpdateUi()
    {
        var hasPages = pages.Count > 0;
        var selected = pages.Count(p => p.IsSelected);

        EmptyState.Visibility = hasPages ? Visibility.Collapsed : Visibility.Visible;
        PreviewScroll.Visibility = hasPages ? Visibility.Visible : Visibility.Collapsed;
        ZoomBar.Visibility = hasPages ? Visibility.Visible : Visibility.Collapsed;
        RotateGrips.Visibility = hasPages && current is not null && !cropping && !painting && !quadding && !busy ? Visibility.Visible : Visibility.Collapsed;
        BrushArea.IsHitTestVisible = !busy;
        QuadArea.IsHitTestVisible = !busy;
        PagesStatus.Text = string.Format("{0} page(s)", pages.Count);
        SelectedStatus.Text = string.Format("{0} selected", selected);
        FolderText.Text = settings.Folder;
        QualityValue.Text = $"{settings.Quality}%";
        DailyPreview.Text = settings.DailyFolders
            ? string.Format("Today's folder: {0}", DailyFolder.Name(settings.DateStyle, DateTime.Now))
            : "Files are saved straight into the folder above.";
        if (!hasPages) DocumentTitle.Text = "Untitled batch";

        ScanButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy;
        AddBlankButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy && selected > 0;
        AppendButton.IsEnabled = !busy && selected > 0;
        ExportButton.IsEnabled = !busy && pages.Count > 0;
        var editable = !busy && current is not null;
        foreach (var control in new Control[] { RotateLeftButton, RotateRightButton, DeleteButton, CropButton, SplitButton, PlaceImageButton, QuadButton, BrushButton, CompareButton, ApplyAllButton, ResetEditsButton,
                                                 ModeMagic, ModeOriginal, ModeGray, ModeBlackWhite, ModeText, BrightnessSlider, HighlightsSlider, ShadowsSlider,
                                                 StraightenSlider, AutoStraightenButton })
            control.IsEnabled = editable;
        AutoStraightenAllButton.IsEnabled = hasPages && !busy;
        ContrastSlider.IsEnabled = editable && current!.Mode != EnhanceMode.BlackWhite; // B&W has no contrast
        SelectAllButton.IsEnabled = hasPages;
        ClearButton.IsEnabled = hasPages && !busy;
        QualitySlider.IsEnabled = settings.Format != OutputFormat.Png;

        foreach (var button in new[] { ModeMagic, ModeOriginal, ModeGray, ModeBlackWhite, ModeText })
            Mark(button, current is not null && (string)button.Tag == current.Mode.ToString());
        foreach (var button in new[] { FormatPdf, FormatJpeg, FormatPng })
            Mark(button, (string)button.Tag == settings.Format.ToString());
        foreach (var button in new[] { ThemeSystem, ThemeLight, ThemeDark })
            Mark(button, (string)button.Tag == settings.Theme.ToString());
        CropButton.Background = cropping ? Res("AccentTintBrush") : new SolidColorBrush(Colors.Transparent);
        QuadButton.Background = quadding ? Res("AccentTintBrush") : new SolidColorBrush(Colors.Transparent);
        BrushButton.Background = painting ? Res("AccentTintBrush") : new SolidColorBrush(Colors.Transparent);
    }

    /// <summary>Highlights a tile button as the selected option.</summary>
    private void Mark(Button button, bool active)
    {
        button.BorderBrush = active ? Accent : Res("BorderBrush2");
        button.BorderThickness = new Thickness(active ? 2 : 1);
        button.Background = active ? Res("AccentTintBrush") : Res("TileBrush");
    }

    private bool toastOpensFile;

    /// <param name="revealPath">File the toast's button acts on: shown in Explorer, or opened when <paramref name="openFile"/> is set.</param>
    private void ShowToast(string message, string? revealPath = null, string? actionText = null, bool openFile = false)
    {
        toastPath = revealPath;
        toastOpensFile = openFile;
        ToastAction.Content = actionText ?? "Show in folder";
        ToastText.Text = message;
        ToastAction.Visibility = revealPath is null ? Visibility.Collapsed : Visibility.Visible;
        Toast.Visibility = Visibility.Visible;
        toastTimer.Stop();
        toastTimer.Start();
    }

    private void HideToast()
    {
        toastTimer.Stop();
        Toast.Visibility = Visibility.Collapsed;
    }

    private void ToastActionClick(object sender, RoutedEventArgs e)
    {
        if (toastPath is not null && File.Exists(toastPath))
        {
            if (toastOpensFile) Process.Start(new ProcessStartInfo(toastPath) { UseShellExecute = true });
            else Process.Start("explorer.exe", $"/select,\"{toastPath}\"");
        }
        HideToast();
    }
}
