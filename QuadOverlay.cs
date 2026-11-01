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
/// Four draggable corner handles joined by an outline, drawn over the page preview (place it over the image, same size).
/// Used to mark where the page really is when the automatic edge finder got it wrong. Corners are fractions (0..1) of
/// the page, in the order top-left, top-right, bottom-right, bottom-left.
/// </summary>
public sealed class QuadOverlay : Canvas
{
    private const double HandleSize = 22;

    private readonly Polygon outline;
    private readonly Border[] handles = new Border[4];
    private readonly (double X, double Y)[] corners = [(0, 0), (1, 0), (1, 1), (0, 1)];
    private double scale = 1;
    private int dragging = -1;

    public QuadOverlay()
    {
        outline = new Polygon
        {
            Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 0x26, 0x80, 0xEB)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(ColorHelper.FromArgb(40, 0x26, 0x80, 0xEB)),
            IsHitTestVisible = false,
        };
        Children.Add(outline);

        for (var i = 0; i < handles.Length; i++)
        {
            var index = i;
            var handle = new Border
            {
                Width = HandleSize,
                Height = HandleSize,
                CornerRadius = new CornerRadius(HandleSize / 2),
                Background = new SolidColorBrush(ColorHelper.FromArgb(235, 0x26, 0x80, 0xEB)),
                BorderBrush = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(2),
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            ToolTipService.SetToolTip(handle, "Drag to the corner of the page");
            handle.PointerPressed += (_, e) => OnPressed(index, handle, e);
            handle.PointerMoved += (_, e) => OnMoved(index, e);
            handle.PointerReleased += (_, e) => OnReleased(handle, e);
            handle.PointerCaptureLost += (_, _) => dragging = -1;
            handles[i] = handle;
            Children.Add(handle);
        }
        SizeChanged += (_, _) => Relayout();
    }

    /// <summary>Raised when a corner has been moved.</summary>
    public event Action? Changed;

    /// <summary>The four corners as fractions of the page: top-left, top-right, bottom-right, bottom-left.</summary>
    public IReadOnlyList<(double X, double Y)> Corners => corners;

    /// <summary>Keeps the handles the same on-screen size when the preview is zoomed (pass 1 / zoom factor).</summary>
    public double HandleScale
    {
        set
        {
            scale = Math.Clamp(value, 0.1, 1);
            foreach (var handle in handles) handle.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
            Relayout();
        }
    }

    public void SetCorners(IReadOnlyList<(double X, double Y)> value)
    {
        for (var i = 0; i < 4; i++) corners[i] = value[i];
        Relayout();
        Changed?.Invoke();
    }

    /// <summary>Puts the corners at the edges of the whole page.</summary>
    public void SelectAll() => SetCorners([(0, 0), (1, 0), (1, 1), (0, 1)]);

    private void Relayout()
    {
        var points = new PointCollection();
        for (var i = 0; i < 4; i++)
        {
            var p = new Point(corners[i].X * ActualWidth, corners[i].Y * ActualHeight);
            points.Add(p);
            SetLeft(handles[i], p.X - HandleSize / 2);
            SetTop(handles[i], p.Y - HandleSize / 2);
        }
        outline.Points = points;
        outline.StrokeThickness = 2 * scale;
    }

    private void OnPressed(int index, UIElement handle, PointerRoutedEventArgs e)
    {
        // Space + drag pans the page instead (handled by the window).
        if (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Space).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        if (!handle.CapturePointer(e.Pointer)) return;
        dragging = index;
        e.Handled = true;
    }

    private void OnMoved(int index, PointerRoutedEventArgs e)
    {
        if (dragging != index) return;
        var p = e.GetCurrentPoint(this).Position;
        corners[index] = (Math.Clamp(p.X / Math.Max(ActualWidth, 1), 0, 1), Math.Clamp(p.Y / Math.Max(ActualHeight, 1), 0, 1));
        Relayout();
        Changed?.Invoke();
        e.Handled = true;
    }

    private void OnReleased(UIElement handle, PointerRoutedEventArgs e)
    {
        if (dragging < 0) return;
        dragging = -1;
        handle.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }
}
