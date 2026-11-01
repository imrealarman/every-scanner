using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SScanner.Models;
using SScanner.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SScanner;

// ---- Blank page, and placing an image on a page ----
public sealed partial class MainWindow
{
    private sealed record Placement(ImageEnhancer.PaperSize Paper, bool Landscape, Func<byte[], byte[]> Apply);

    private async void AddBlankClick(object sender, RoutedEventArgs e) => await AddBlankPageAsync();

    private async void PlaceImageClick(object sender, RoutedEventArgs e) => await PlaceOnCurrentAsync();

    /// <summary>Adds a white page after the current one, with an optional picture placed on it.</summary>
    private async Task AddBlankPageAsync()
    {
        if (busy) return;
        ExitTools();
        var placement = await ChoosePlacementAsync("Blank page", "Add page", null);
        if (placement is null) return;

        SetBusy(true, "Making the page…");
        try
        {
            var bytes = await Task.Run(() => placement.Apply(ImageEnhancer.BlankPage(placement.Paper, placement.Landscape)));
            var page = new ScanPage(bytes);
            page.Rendered += WarmPdf;
            page.SelectionChanged += UpdateUi;
            await page.RenderAsync();
            var at = current is null ? pages.Count : pages.IndexOf(current) + 1;
            pages.Insert(at, page);
            SetCurrent(page);
            ShowToast("Blank page added. Ctrl+Z undoes it.");
        }
        catch (Exception ex)
        {
            Log.Error("Blank page", ex);
            ShowToast("Could not add the page.");
        }
        finally { SetBusy(false); }
    }

    /// <summary>Places a picture on the page that is open now.</summary>
    private async Task PlaceOnCurrentAsync()
    {
        var page = current;
        if (page is null || busy) return;
        ExitTools();

        var baked = await page.BakedAsync();
        var placement = await ChoosePlacementAsync("Place an image on this page", "Place", baked);
        if (placement is null || page != current) return;

        SetBusy(true, "Placing the image…");
        try
        {
            await page.PlaceAsync(placement.Apply);
            ShowToast("Image placed. Ctrl+Z undoes it.");
        }
        catch (Exception ex)
        {
            Log.Error("Place image", ex);
            ShowToast("Could not place the image.");
        }
        finally { SetBusy(false); }
    }

    private async Task<byte[]?> PickImageAsync()
    {
        var picker = new FileOpenPicker();
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" })
            picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        return file is null ? null : await File.ReadAllBytesAsync(file.Path);
    }

    /// <summary>
    /// Shows the placement dialog: a live preview of the page you can drag the picture around on, plus size (mm), direction, tilt
    /// and look. <paramref name="existingPage"/> is the page to place onto, or null for a new blank page.
    /// </summary>
    private async Task<Placement?> ChoosePlacementAsync(string title, string primary, byte[]? existingPage)
    {
        var blank = existingPage is null;
        var landscape = false;
        var paper = ImageEnhancer.PaperSizes[4]; // A4
        byte[] pageBytes = existingPage ?? ImageEnhancer.BlankPage(paper, false);
        var pageSize = blank ? ImageEnhancer.PaperPixels(paper, false) : ImageEnhancer.SizeOf(pageBytes);

        byte[]? image = null;
        double aspect = 1; // picture height / width
        double cx = 0.5, cy = 0.5, widthMm = 100, heightMm = 100;
        var updating = false;

        // Page size in millimetres: the chosen paper for a blank page, otherwise the open page taken as 210 mm wide.
        (double W, double H) PageMm() => blank
            ? (landscape ? (paper.HeightMm, paper.WidthMm) : (paper.WidthMm, paper.HeightMm))
            : (210.0, 210.0 * pageSize.Height / pageSize.Width);

        var muted = (Style)Application.Current.Resources["MutedText"];
        TextBlock Label(string text) => new() { Text = text, Style = muted };

        var preview = new Image { Height = 440, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };

        var chooseButton = new Button { Content = "Choose image…", HorizontalAlignment = HorizontalAlignment.Stretch };
        var paperBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 13, SelectedIndex = 4, Visibility = blank ? Visibility.Visible : Visibility.Collapsed };
        foreach (var size in ImageEnhancer.PaperSizes) paperBox.Items.Add($"{size.Name}  ({size.WidthMm:0.#} × {size.HeightMm:0.#} mm)");
        paperBox.Items.Add("Custom size…");
        var customWidth = new NumberBox { Header = "Page width (mm)", Minimum = 20, Maximum = 1500, Value = 210, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, HorizontalAlignment = HorizontalAlignment.Stretch };
        var customHeight = new NumberBox { Header = "Page height (mm)", Minimum = 20, Maximum = 1500, Value = 297, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, HorizontalAlignment = HorizontalAlignment.Stretch };
        var customPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        customPanel.Children.Add(customWidth);
        customPanel.Children.Add(customHeight);
        var orientation = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 13, SelectedIndex = 0, Visibility = blank ? Visibility.Visible : Visibility.Collapsed };
        orientation.Items.Add("Page: portrait");
        orientation.Items.Add("Page: landscape");

        var widthBox = new NumberBox { Header = "Width (mm)", Minimum = 5, Maximum = 2000, SmallChange = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, HorizontalAlignment = HorizontalAlignment.Stretch };
        var heightBox = new NumberBox { Header = "Height (mm)", Minimum = 5, Maximum = 2000, SmallChange = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, HorizontalAlignment = HorizontalAlignment.Stretch };
        var keepRatio = new CheckBox { Content = "Keep proportions", IsChecked = true };

        var direction = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 13, SelectedIndex = 0, Header = "Direction" };
        foreach (var name in new[] { "Upright", "Turned 90° right", "Upside down", "Turned 90° left" }) direction.Items.Add(name);
        var tilt = new Slider { Minimum = -45, Maximum = 45, Value = 0, StepFrequency = 0.5 };
        var tiltText = Label("Tilt 0°");

        var fit = new Button { Content = "Fit page" };
        var fill = new Button { Content = "Fill page" };
        var center = new Button { Content = "Center" };

        var look = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 13, SelectedIndex = 0, Header = "Effect" };
        foreach (var name in new[] { "Original", "Magic color", "Gray", "Black & white", "Text" }) look.Items.Add(name);
        var brightness = new Slider { Minimum = -100, Maximum = 100, Value = 0 };
        var contrast = new Slider { Minimum = -100, Maximum = 100, Value = 0 };

        EnhanceMode Mode() => look.SelectedIndex switch
        {
            1 => EnhanceMode.Magic,
            2 => EnhanceMode.Gray,
            3 => EnhanceMode.BlackWhite,
            4 => EnhanceMode.Text,
            _ => EnhanceMode.Original,
        };
        double Angle() => direction.SelectedIndex switch { 1 => -90, 2 => 180, 3 => 90, _ => 0 } + tilt.Value;

        void ShowBoxes()
        {
            updating = true;
            widthBox.Value = Math.Round(widthMm, 1);
            heightBox.Value = Math.Round(heightMm, 1);
            updating = false;
        }

        // Scales the picture, keeping its proportions, until it fits inside the page (or covers it), and centres it.
        void ScaleToPage(bool cover)
        {
            var (pw, ph) = PageMm();
            var a = Angle() * Math.PI / 180;
            var (cos, sin) = (Math.Abs(Math.Cos(a)), Math.Abs(Math.Sin(a)));
            var (w, h) = (100.0, 100.0 * aspect);
            var (bw, bh) = (w * cos + h * sin, w * sin + h * cos);
            var k = cover ? Math.Max(pw / bw, ph / bh) : Math.Min(pw / bw, ph / bh);
            widthMm = w * k;
            heightMm = h * k;
            (cx, cy) = (0.5, 0.5);
            ShowBoxes();
        }

        var imageRows = new StackPanel { Spacing = 8 };
        var imageGroup = new ContentControl { Content = imageRows, IsEnabled = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        imageRows.Children.Add(widthBox);
        imageRows.Children.Add(heightBox);
        imageRows.Children.Add(keepRatio);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(fit);
        buttons.Children.Add(fill);
        buttons.Children.Add(center);
        imageRows.Children.Add(buttons);
        imageRows.Children.Add(direction);
        imageRows.Children.Add(tiltText);
        imageRows.Children.Add(tilt);
        imageRows.Children.Add(look);
        imageRows.Children.Add(Label("Brightness"));
        imageRows.Children.Add(brightness);
        imageRows.Children.Add(Label("Contrast"));
        imageRows.Children.Add(contrast);

        var controls = new StackPanel { Spacing = 8, Width = 290 };
        controls.Children.Add(paperBox);
        controls.Children.Add(customPanel);
        controls.Children.Add(orientation);
        controls.Children.Add(chooseButton);
        controls.Children.Add(Label("Drag the picture on the page to move it."));
        controls.Children.Add(imageGroup);

        var root = new Grid { ColumnSpacing = 16 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var previewFrame = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(40, 128, 128, 128)),
            Child = preview,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var scroller = new ScrollViewer { Content = controls, MaxHeight = 470, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
        Grid.SetColumn(scroller, 1);
        root.Children.Add(previewFrame);
        root.Children.Add(scroller);

        var dialog = new ContentDialog
        {
            Title = title,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = blank,
            Content = root,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 900.0;

        // Live preview: only one render runs at a time; if things changed meanwhile it runs again with the latest values.
        var rendering = false;
        var dirty = false;
        async void Refresh()
        {
            dirty = true;
            if (rendering) return;
            rendering = true;
            try
            {
                while (dirty)
                {
                    dirty = false;
                    var (page, picture, mode, b, c, x, y, angle) = (pageBytes, image, Mode(), (int)brightness.Value, (int)contrast.Value, cx, cy, Angle());
                    var (pw, ph) = PageMm();
                    var (wFrac, hFrac) = (widthMm / pw, heightMm / ph);
                    var scale = Math.Min(1.0, 800.0 / Math.Max(pageSize.Width, pageSize.Height));
                    var bytes = picture is null
                        ? page
                        : await Task.Run(() => ImageEnhancer.PlaceImage(page, picture, mode, b, c, x, y, wFrac, hFrac, angle, scale));
                    preview.Source = await ImageHelper.ToBitmapAsync(bytes, 800);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Place preview", ex);
            }
            finally { rendering = false; }
        }

        chooseButton.Click += async (_, _) =>
        {
            byte[]? picked;
            try { picked = await PickImageAsync(); }
            catch (Exception ex) { Log.Error("Pick image", ex); return; }
            if (picked is null) return;
            try
            {
                var size = await Task.Run(() => ImageEnhancer.SizeOf(picked));
                image = picked;
                aspect = size.Height / (double)size.Width;
            }
            catch (Exception ex)
            {
                Log.Error("Read image", ex);
                ShowToast("Could not read that image.");
                return;
            }
            imageGroup.IsEnabled = true;
            dialog.IsPrimaryButtonEnabled = true;
            chooseButton.Content = "Change image…";
            ScaleToPage(false);
            Refresh();
        };
        void PageChanged()
        {
            landscape = orientation.SelectedIndex == 1;
            var custom = paperBox.SelectedIndex >= ImageEnhancer.PaperSizes.Length;
            customPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
            if (custom)
            {
                if (double.IsNaN(customWidth.Value) || double.IsNaN(customHeight.Value)) return;
                paper = new ImageEnhancer.PaperSize("Custom", customWidth.Value, customHeight.Value);
            }
            else if (paperBox.SelectedIndex >= 0)
                paper = ImageEnhancer.PaperSizes[paperBox.SelectedIndex];
            pageBytes = ImageEnhancer.BlankPage(paper, landscape);
            pageSize = ImageEnhancer.PaperPixels(paper, landscape);
            if (image is not null) ScaleToPage(false);
            Refresh();
        }
        orientation.SelectionChanged += (_, _) => PageChanged();
        paperBox.SelectionChanged += (_, _) => PageChanged();
        customWidth.ValueChanged += (_, _) => PageChanged();
        customHeight.ValueChanged += (_, _) => PageChanged();
        widthBox.ValueChanged += (_, _) =>
        {
            if (updating || double.IsNaN(widthBox.Value)) return;
            widthMm = widthBox.Value;
            if (keepRatio.IsChecked == true) heightMm = widthMm * aspect;
            ShowBoxes();
            Refresh();
        };
        heightBox.ValueChanged += (_, _) =>
        {
            if (updating || double.IsNaN(heightBox.Value)) return;
            heightMm = heightBox.Value;
            if (keepRatio.IsChecked == true) widthMm = heightMm / aspect;
            ShowBoxes();
            Refresh();
        };
        keepRatio.Click += (_, _) =>
        {
            if (keepRatio.IsChecked != true) return;
            heightMm = widthMm * aspect;
            ShowBoxes();
            Refresh();
        };
        direction.SelectionChanged += (_, _) => Refresh();
        tilt.ValueChanged += (_, _) => { tiltText.Text = $"Tilt {tilt.Value:0.#}°"; Refresh(); };
        look.SelectionChanged += (_, _) => Refresh();
        brightness.ValueChanged += (_, _) => Refresh();
        contrast.ValueChanged += (_, _) => Refresh();
        fit.Click += (_, _) => { ScaleToPage(false); Refresh(); };
        fill.Click += (_, _) => { ScaleToPage(true); Refresh(); };
        center.Click += (_, _) => { (cx, cy) = (0.5, 0.5); Refresh(); };

        // Dragging the picture on the preview moves it.
        Windows.Foundation.Point? last = null;
        preview.PointerPressed += (_, e) =>
        {
            if (image is null || !preview.CapturePointer(e.Pointer)) return;
            last = e.GetCurrentPoint(preview).Position;
        };
        preview.PointerMoved += (_, e) =>
        {
            if (last is not { } from || preview.ActualWidth < 1 || preview.ActualHeight < 1) return;
            var now = e.GetCurrentPoint(preview).Position;
            cx = Math.Clamp(cx + (now.X - from.X) / preview.ActualWidth, 0, 1);
            cy = Math.Clamp(cy + (now.Y - from.Y) / preview.ActualHeight, 0, 1);
            last = now;
            Refresh();
        };
        preview.PointerReleased += (_, e) => { last = null; preview.ReleasePointerCapture(e.Pointer); };
        preview.PointerCaptureLost += (_, _) => last = null;

        Refresh();
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return null;

        var (finalImage, finalMode, b, c) = (image, Mode(), (int)brightness.Value, (int)contrast.Value);
        var (x, y, angle) = (cx, cy, Angle());
        var (pageW, pageH) = PageMm();
        var (wf, hf) = (widthMm / pageW, heightMm / pageH);
        return new Placement(paper, landscape, finalImage is null
            ? basePage => basePage
            : basePage => ImageEnhancer.PlaceImage(basePage, finalImage, finalMode, b, c, x, y, wf, hf, angle));
    }

    // ---- Export as JPEG ----

    private async void ExportClick(object sender, RoutedEventArgs e) => await ExportJpegAsync();

    /// <summary>Writes all pages, or just the selected ones, as JPEG files. Unlike Save, the pages stay in the batch.</summary>
    private async Task ExportJpegAsync()
    {
        if (busy || pages.Count == 0) return;
        ExitTools();

        var selectedCount = pages.Count(p => p.IsSelected);
        var which = new RadioButtons { SelectedIndex = selectedCount > 0 && selectedCount < pages.Count ? 1 : 0 };
        which.Items.Add($"All pages ({pages.Count})");
        which.Items.Add($"Selected pages only ({selectedCount})");
        var nameBox = new TextBox { Text = $"Scan {DateTime.Now:yyyy-MM-dd HH-mm}", PlaceholderText = "File name", FontSize = 13 };
        nameBox.Loaded += (_, _) => { nameBox.Focus(FocusState.Programmatic); nameBox.SelectAll(); };
        var info = new TextBlock
        {
            Style = (Style)Application.Current.Resources["MutedText"],
            TextWrapping = TextWrapping.Wrap,
            Text = $"JPEG quality {settings.Quality}% (change it in the settings panel). One file per page, saved in {settings.TargetFolder(DateTime.Now)}",
        };

        var content = new StackPanel { Spacing = 10, MinWidth = 380 };
        content.Children.Add(which);
        content.Children.Add(nameBox);
        content.Children.Add(info);
        var dialog = new ContentDialog
        {
            Title = "Export as JPEG",
            PrimaryButtonText = "Export",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var chosen = which.SelectedIndex == 1 ? pages.Where(p => p.IsSelected).ToList() : pages.ToList();
        var name = nameBox.Text.Trim();
        if (chosen.Count == 0 || name.Length == 0) { ShowToast("Nothing to export."); return; }

        SetBusy(true, "Exporting…");
        try
        {
            var data = chosen.Select(p => p.Processed).ToList();
            var (quality, folder) = (settings.Quality, settings.TargetFolder(DateTime.Now));
            var path = await Task.Run(() => OutputService.Save(data, OutputFormat.Jpeg, quality, folder, name));
            ShowToast(data.Count > 1 ? $"Exported {data.Count} JPEG images to {Path.GetFileName(folder)}" : $"Exported {Path.GetFileName(path)}", path);
            _ = index.RefreshAsync(settings.Folder);
        }
        catch (Exception ex)
        {
            Log.Error("Export JPEG", ex);
            ShowToast($"Could not export: {ex.Message}", Log.CurrentFile, "Open log", openFile: true);
        }
        finally { SetBusy(false); }
    }
}
