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
/// Brush for dodge and burn, drawn over the page preview (place it on top of the image, same size).
/// Shows a ring the size of the brush under the pointer; press and drag to paint. While a stroke is still being
/// applied to the page it stays visible as a faint trail, until <see cref="ClearTrails"/> is called.
/// </summary>
public sealed class BrushOverlay : Canvas
{
    private const double MinPointGap = 0.002; // fraction of the page width: closer points add nothing

    private readonly Ellipse ringOuter;
    private readonly Ellipse ringInner;
    private readonly List<Polyline> trails = [];

    private Polyline? live;
    private List<(double X, double Y)>? points;

    public BrushOverlay()
    {
        Background = new SolidColorBrush(Colors.Transparent); // needed so empty areas receive pointer input
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);

        ringOuter = Ring(Colors.Black);
        ringInner = Ring(Colors.White);
        Children.Add(ringOuter);
        Children.Add(ringInner);

        PointerEntered += (_, e) => MoveRing(e);
        PointerExited += (_, _) => ShowRing(false);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => points = null;
        SizeChanged += (_, _) => ResizeRing();
    }

    /// <summary>Brush diameter as a fraction of the page width.</summary>
    public double Diameter
    {
        get => diameter;
        set { diameter = Math.Clamp(value, 0.002, 0.5); ResizeRing(); }
    }

    private double diameter = 0.04;

    /// <summary>Colour of the faint trail a stroke leaves while it is being applied.</summary>
    public Windows.UI.Color TrailColor { get; set; } = Colors.White;

    /// <summary>Raised when the pointer is released after painting, with the stroke's points as fractions (0..1) of the page.</summary>
    public event Action<IReadOnlyList<(double X, double Y)>>? StrokeCompleted;

    /// <summary>Removes the trails of strokes that have now been applied to the page.</summary>
    public void ClearTrails()
    {
        foreach (var trail in trails) Children.Remove(trail);
        trails.Clear();
    }

    private static Ellipse Ring(Windows.UI.Color colour) => new()
    {
        Stroke = new SolidColorBrush(colour),
        StrokeThickness = 1,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false,
    };

    private double Px => Diameter * ActualWidth;

    private void ResizeRing()
    {
        ringOuter.Width = ringOuter.Height = Px + 2;
        ringInner.Width = ringInner.Height = Px;
    }

    private void ShowRing(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        ringOuter.Visibility = ringInner.Visibility = v;
    }

    private void MoveRing(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Position;
        ResizeRing();
        SetLeft(ringOuter, p.X - ringOuter.Width / 2);
        SetTop(ringOuter, p.Y - ringOuter.Height / 2);
        SetLeft(ringInner, p.X - ringInner.Width / 2);
        SetTop(ringInner, p.Y - ringInner.Height / 2);
        ShowRing(true);
    }

    private (double X, double Y) Fraction(Point p) =>
        (Math.Clamp(p.X / Math.Max(ActualWidth, 1), 0, 1), Math.Clamp(p.Y / Math.Max(ActualHeight, 1), 0, 1));

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        // Space + drag pans the page instead (handled by the window).
        if (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Space).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || !CapturePointer(e.Pointer)) return;

        points = [Fraction(point.Position)];
        live = new Polyline
        {
            Stroke = new SolidColorBrush(TrailColor),
            Opacity = 0.35,
            StrokeThickness = Math.Max(Px, 1),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        live.Points.Add(point.Position);
        Children.Insert(0, live);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        MoveRing(e);
        if (points is null || live is null) return;
        var position = e.GetCurrentPoint(this).Position;
        var f = Fraction(position);
        var last = points[^1];
        if (Math.Abs(f.X - last.X) < MinPointGap && Math.Abs(f.Y - last.Y) < MinPointGap) return;
        points.Add(f);
        live.Points.Add(position);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (points is null) return;
        // Take the points before releasing: releasing fires PointerCaptureLost, which clears them.
        var stroke = points;
        points = null;
        ReleasePointerCapture(e.Pointer);
        if (live is not null) trails.Add(live);
        live = null;
        StrokeCompleted?.Invoke(stroke);
        e.Handled = true;
    }
}
