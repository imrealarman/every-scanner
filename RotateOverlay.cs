using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace SScanner;

/// <summary>
/// Round grips on the four corners of the page preview (place it over the image, same size).
/// Drag a grip around the page centre to turn the page a little; the angle moved is reported in degrees,
/// clockwise positive.
/// </summary>
public sealed class RotateOverlay : Canvas
{
    private const double GripSize = 24;
    private const double Inset = 18;

    private readonly Border[] grips = new Border[4];
    private double scale = 1;
    private bool dragging;
    private double startAngle;

    public RotateOverlay()
    {
        for (var i = 0; i < grips.Length; i++)
        {
            var grip = new Border
            {
                Width = GripSize,
                Height = GripSize,
                CornerRadius = new CornerRadius(GripSize / 2),
                Background = new SolidColorBrush(ColorHelper.FromArgb(215, 0x26, 0x80, 0xEB)),
                BorderBrush = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(1.5),
                RenderTransformOrigin = new Point(0.5, 0.5),
                Child = new FontIcon
                {
                    Glyph = "",
                    FontSize = 12,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    Foreground = new SolidColorBrush(Colors.White),
                },
            };
            ToolTipService.SetToolTip(grip, "Drag to rotate the page a little");
            grip.PointerPressed += OnPressed;
            grip.PointerMoved += OnMoved;
            grip.PointerReleased += OnReleased;
            grip.PointerCaptureLost += (_, _) => EndDrag();
            grips[i] = grip;
            Children.Add(grip);
        }
        SizeChanged += (_, _) => Relayout();
    }

    /// <summary>Raised when a grip is pressed, before any rotation.</summary>
    public event Action? DragStarted;

    /// <summary>Raised while dragging, with the angle turned since the press (degrees, clockwise positive).</summary>
    public event Action<double>? Rotated;

    /// <summary>Keeps the grips the same on-screen size when the preview is zoomed (pass 1 / zoom factor).</summary>
    public double GripScale
    {
        set
        {
            scale = Math.Clamp(value, 0.1, 1);
            foreach (var grip in grips) grip.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
            Relayout();
        }
    }

    private Point Center => new(ActualWidth / 2, ActualHeight / 2);

    private void Relayout()
    {
        var inset = Inset * scale;
        var corners = new[]
        {
            new Point(inset, inset),
            new Point(ActualWidth - inset, inset),
            new Point(inset, ActualHeight - inset),
            new Point(ActualWidth - inset, ActualHeight - inset),
        };
        for (var i = 0; i < grips.Length; i++)
        {
            SetLeft(grips[i], corners[i].X - GripSize / 2);
            SetTop(grips[i], corners[i].Y - GripSize / 2);
        }
    }

    private double AngleOf(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this).Position;
        var c = Center;
        return Math.Atan2(p.Y - c.Y, p.X - c.X) * 180 / Math.PI;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        // Space + drag pans the page instead (handled by the window).
        if (InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Space).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        dragging = ((UIElement)sender).CapturePointer(e.Pointer);
        if (!dragging) return;
        startAngle = AngleOf(e);
        DragStarted?.Invoke();
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!dragging) return;
        var delta = AngleOf(e) - startAngle;
        delta = ((delta + 540) % 360) - 180; // shortest way round, so crossing the 180° line doesn't jump
        Rotated?.Invoke(delta);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!dragging) return;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag() => dragging = false;
}
