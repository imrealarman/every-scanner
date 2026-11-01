using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace SScanner;

/// <summary>
/// Crop selection drawn over the page preview (place it on top of the image, same size).
/// Press and drag anywhere to draw a selection; drag inside it to move, drag an edge or corner to resize,
/// drag outside it to start again. Double-click inside to apply.
/// </summary>
public sealed class CropOverlay : Canvas
{
    [Flags]
    private enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

    private const double HandleSize = 12;
    private const double HitSlop = 14;
    private const double MinSize = 24;

    private readonly Rectangle[] dim = new Rectangle[4];
    private readonly Rectangle frame;
    private readonly Rectangle[] handles = new Rectangle[8];
    private readonly Line[] thirds = new Line[4];
    private readonly Border sizeLabel;
    private readonly TextBlock sizeText;
    private readonly Dictionary<InputSystemCursorShape, InputCursor> cursors = [];

    private double left, top, right, bottom;
    private bool hasSelection;
    private bool dragging;
    private bool drawingNew;
    private Edge dragMode;
    private Point dragStart;
    private (double L, double T, double R, double B) dragOrigin;

    public CropOverlay()
    {
        Background = new SolidColorBrush(Colors.Transparent); // needed so empty areas receive pointer input

        var dimBrush = new SolidColorBrush(ColorHelper.FromArgb(150, 0, 0, 0));
        for (var i = 0; i < dim.Length; i++)
        {
            dim[i] = new Rectangle { Fill = dimBrush, IsHitTestVisible = false };
            Children.Add(dim[i]);
        }

        var guide = new SolidColorBrush(ColorHelper.FromArgb(90, 255, 255, 255));
        for (var i = 0; i < thirds.Length; i++)
        {
            thirds[i] = new Line { Stroke = guide, StrokeThickness = 1, IsHitTestVisible = false };
            Children.Add(thirds[i]);
        }

        frame = new Rectangle { Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 1.5, IsHitTestVisible = false };
        Children.Add(frame);

        for (var i = 0; i < handles.Length; i++)
        {
            handles[i] = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = new SolidColorBrush(ColorHelper.FromArgb(255, 0x26, 0x80, 0xEB)),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = 1.5,
                IsHitTestVisible = false,
            };
            Children.Add(handles[i]);
        }

        sizeText = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Colors.White) };
        sizeLabel = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(210, 0, 0, 0)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Child = sizeText,
            IsHitTestVisible = false,
        };
        Children.Add(sizeLabel);

        SizeChanged += OnSizeChanged;
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => EndDrag();
        DoubleTapped += (_, e) =>
        {
            if (hasSelection && HitTest(e.GetPosition(this)) == Edge.Move)
                ApplyRequested?.Invoke();
        };

        Relayout();
    }

    /// <summary>True once the user has drawn (or chosen) an area.</summary>
    public bool HasSelection => hasSelection;

    /// <summary>Real pixel size of the page being cropped; only used for the size label.</summary>
    public (int Width, int Height) PagePixels { get; set; }

    /// <summary>Raised when the selection appears, changes after a drag, or is cleared.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised when the user double-clicks inside the selection.</summary>
    public event Action? ApplyRequested;

    /// <summary>The selection as fractions (0..1) of the page; the whole page if nothing is selected.</summary>
    public (double X, double Y, double W, double H) Normalized =>
        !hasSelection || ActualWidth <= 0 || ActualHeight <= 0
            ? (0, 0, 1, 1)
            : (left / ActualWidth, top / ActualHeight, (right - left) / ActualWidth, (bottom - top) / ActualHeight);

    /// <summary>Removes the selection so the user can draw a new one.</summary>
    public void Reset()
    {
        hasSelection = false;
        Relayout();
        SelectionChanged?.Invoke();
    }

    /// <summary>Selects the whole page, handy for trimming just one edge.</summary>
    public void SelectAll()
    {
        (left, top, right, bottom) = (0, 0, ActualWidth, ActualHeight);
        hasSelection = true;
        Relayout();
        SelectionChanged?.Invoke();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (hasSelection && e.PreviousSize.Width > 0 && e.PreviousSize.Height > 0)
        {
            var sx = e.NewSize.Width / e.PreviousSize.Width;
            var sy = e.NewSize.Height / e.PreviousSize.Height;
            left *= sx; right *= sx; top *= sy; bottom *= sy;
        }
        Relayout();
    }

    private Edge HitTest(Point p)
    {
        if (!hasSelection) return Edge.None;

        var edge = Edge.None;
        var withinY = p.Y >= top - HitSlop && p.Y <= bottom + HitSlop;
        var withinX = p.X >= left - HitSlop && p.X <= right + HitSlop;
        if (withinY)
        {
            if (Math.Abs(p.X - left) <= HitSlop) edge |= Edge.Left;
            else if (Math.Abs(p.X - right) <= HitSlop) edge |= Edge.Right;
        }
        if (withinX)
        {
            if (Math.Abs(p.Y - top) <= HitSlop) edge |= Edge.Top;
            else if (Math.Abs(p.Y - bottom) <= HitSlop) edge |= Edge.Bottom;
        }
        if (edge != Edge.None) return edge;
        return p.X > left && p.X < right && p.Y > top && p.Y < bottom ? Edge.Move : Edge.None;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        // Space + drag pans the page instead (handled by the window).
        if (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Space).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;

        var p = e.GetCurrentPoint(this).Position;
        dragStart = p;
        dragOrigin = (left, top, right, bottom);
        dragMode = HitTest(p);
        drawingNew = dragMode == Edge.None;
        dragging = true;
        if (drawingNew)
        {
            left = right = Math.Clamp(p.X, 0, ActualWidth);
            top = bottom = Math.Clamp(p.Y, 0, ActualHeight);
            hasSelection = true;
        }
        CapturePointer(e.Pointer);
        Relayout();
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Position;
        if (!dragging)
        {
            SetCursor(HitTest(p));
            return;
        }

        var w = ActualWidth;
        var h = ActualHeight;
        if (drawingNew)
        {
            var x = Math.Clamp(p.X, 0, w);
            var y = Math.Clamp(p.Y, 0, h);
            left = Math.Min(dragStart.X, x); right = Math.Max(dragStart.X, x);
            top = Math.Min(dragStart.Y, y); bottom = Math.Max(dragStart.Y, y);
        }
        else if (dragMode == Edge.Move)
        {
            var width = dragOrigin.R - dragOrigin.L;
            var height = dragOrigin.B - dragOrigin.T;
            left = Math.Clamp(dragOrigin.L + (p.X - dragStart.X), 0, w - width);
            top = Math.Clamp(dragOrigin.T + (p.Y - dragStart.Y), 0, h - height);
            right = left + width;
            bottom = top + height;
        }
        else
        {
            var dx = p.X - dragStart.X;
            var dy = p.Y - dragStart.Y;
            if (dragMode.HasFlag(Edge.Left)) left = Math.Clamp(dragOrigin.L + dx, 0, right - MinSize);
            if (dragMode.HasFlag(Edge.Right)) right = Math.Clamp(dragOrigin.R + dx, left + MinSize, w);
            if (dragMode.HasFlag(Edge.Top)) top = Math.Clamp(dragOrigin.T + dy, 0, bottom - MinSize);
            if (dragMode.HasFlag(Edge.Bottom)) bottom = Math.Clamp(dragOrigin.B + dy, top + MinSize, h);
        }
        Relayout();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        EndDrag();
    }

    private void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        if (drawingNew && (right - left < MinSize || bottom - top < MinSize))
            hasSelection = false; // a plain click, not a drag: no selection
        drawingNew = false;
        dragMode = Edge.None;
        Relayout();
        SelectionChanged?.Invoke();
    }

    private void SetCursor(Edge edge)
    {
        var shape = edge switch
        {
            Edge.None => InputSystemCursorShape.Cross,
            Edge.Move => InputSystemCursorShape.SizeAll,
            Edge.Left or Edge.Right => InputSystemCursorShape.SizeWestEast,
            Edge.Top or Edge.Bottom => InputSystemCursorShape.SizeNorthSouth,
            Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => InputSystemCursorShape.SizeNorthwestSoutheast,
            _ => InputSystemCursorShape.SizeNortheastSouthwest,
        };
        if (!cursors.TryGetValue(shape, out var cursor))
            cursors[shape] = cursor = InputSystemCursor.Create(shape);
        ProtectedCursor = cursor;
    }

    private void Relayout()
    {
        var w = ActualWidth;
        var h = ActualHeight;

        if (!hasSelection)
        {
            // Nothing chosen yet: a light veil over the whole page and a crosshair cursor invite dragging.
            Place(dim[0], 0, 0, w, h);
            for (var i = 1; i < dim.Length; i++) Place(dim[i], 0, 0, 0, 0);
            frame.Visibility = Visibility.Collapsed;
            sizeLabel.Visibility = Visibility.Collapsed;
            foreach (var handle in handles) handle.Visibility = Visibility.Collapsed;
            foreach (var line in thirds) line.Visibility = Visibility.Collapsed;
            return;
        }

        Place(dim[0], 0, 0, w, top);
        Place(dim[1], 0, bottom, w, h - bottom);
        Place(dim[2], 0, top, left, bottom - top);
        Place(dim[3], right, top, w - right, bottom - top);
        Place(frame, left, top, right - left, bottom - top);
        frame.Visibility = Visibility.Visible;

        var cx = (left + right) / 2;
        var cy = (top + bottom) / 2;
        Point[] anchors = [new(left, top), new(cx, top), new(right, top), new(right, cy), new(right, bottom), new(cx, bottom), new(left, bottom), new(left, cy)];
        for (var i = 0; i < handles.Length; i++)
        {
            handles[i].Visibility = Visibility.Visible;
            SetLeft(handles[i], anchors[i].X - HandleSize / 2);
            SetTop(handles[i], anchors[i].Y - HandleSize / 2);
        }

        // Rule-of-thirds guides make it easier to line the crop up with the content.
        var third = (right - left) / 3;
        var tall = (bottom - top) / 3;
        SetLine(thirds[0], left + third, top, left + third, bottom);
        SetLine(thirds[1], left + 2 * third, top, left + 2 * third, bottom);
        SetLine(thirds[2], left, top + tall, right, top + tall);
        SetLine(thirds[3], left, top + 2 * tall, right, top + 2 * tall);

        UpdateSizeLabel(w, h);
    }

    private void UpdateSizeLabel(double w, double h)
    {
        if (PagePixels.Width > 0 && w > 0 && h > 0)
        {
            var pw = (int)Math.Round((right - left) / w * PagePixels.Width);
            var ph = (int)Math.Round((bottom - top) / h * PagePixels.Height);
            sizeText.Text = $"{pw} × {ph} px";
        }
        else
        {
            sizeText.Text = $"{(right - left) / w:P0} × {(bottom - top) / h:P0}";
        }

        sizeLabel.Visibility = Visibility.Visible;
        sizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelHeight = sizeLabel.DesiredSize.Height;
        SetLeft(sizeLabel, Math.Clamp(left, 0, Math.Max(0, w - sizeLabel.DesiredSize.Width)));
        SetTop(sizeLabel, top - labelHeight - 6 >= 0 ? top - labelHeight - 6 : top + 6);
    }

    private static void SetLine(Line line, double x1, double y1, double x2, double y2)
    {
        line.X1 = x1; line.Y1 = y1; line.X2 = x2; line.Y2 = y2;
        line.Visibility = Visibility.Visible;
    }

    private static void Place(Rectangle rect, double x, double y, double width, double height)
    {
        SetLeft(rect, x);
        SetTop(rect, y);
        rect.Width = Math.Max(0, width);
        rect.Height = Math.Max(0, height);
        rect.Visibility = Visibility.Visible;
    }
}
