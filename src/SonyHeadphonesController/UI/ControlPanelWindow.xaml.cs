using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Media;

namespace SonyCtrl.UI;

public partial class ControlPanelWindow : Window
{
    public ControlPanelWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    /// <summary>Anchor the flyout above the taskbar, right-aligned (near the tray corner).</summary>
    public void PositionNearTray()
    {
        // Before first Show, Window.Measure returns DesiredSize (0,0) — computing Left/Top
        // from it pushes the window off the bottom-right corner. The window Width is fixed
        // in XAML; for the height, measure the Card element directly (its DesiredSize
        // includes its Margin, so it equals the window height once laid out).
        var workArea = SystemParameters.WorkArea;
        double width = Width;
        double height = ActualHeight;
        if (height <= 0)
        {
            Card.Measure(new Size(width, workArea.Height));
            height = Card.DesiredSize.Height;
        }

        Left = workArea.Right - width - 12;
        Top = workArea.Bottom - height - 8;
    }

    /// <summary>Call on every show: fade + slight scale-in. Loaded only fires once.</summary>
    public void PlayEntranceAnimation()
    {
        // Build a fresh mutable ScaleTransform each time (the Style's Setter freezes its value).
        var scale = new ScaleTransform(0.98, 0.98);
        Card.RenderTransformOrigin = new Point(0.5, 1);
        Card.RenderTransform = scale;
        Card.Opacity = 0;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(180);

        var fade = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
        Card.BeginAnimation(OpacityProperty, fade);

        var sx = new DoubleAnimation(0.98, 1, dur) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        var sy = new DoubleAnimation(0.98, 1, dur) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        // Flyout behaviour: hide when it loses focus (click anywhere else).
        base.Hide();
        // Release Topmost so it doesn't linger over other windows while hidden.
        Topmost = false;
    }

    private void OnAmbientDragCompleted(object sender, DragCompletedEventArgs e)
    {
        // Commit the slider value only when the user releases the thumb: avoids flooding
        // the device with per-tick SET commands during the drag.
        (DataContext as MainViewModel)?.CommitAmbientLevel();
    }
}
