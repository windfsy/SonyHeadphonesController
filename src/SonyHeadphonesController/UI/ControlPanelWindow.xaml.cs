using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Threading;

namespace SonyCtrl.UI;

public partial class ControlPanelWindow : Window
{
    private readonly DispatcherTimer _ambientCommitTimer;
    private Border? _sliderFill;
    private bool _scrubbing;

    public ControlPanelWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // Bar-style slider has no visible thumb, so there is no DragCompleted to commit on.
        // Debounce ValueChanged instead: send once the value settles after a drag/click.
        _ambientCommitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _ambientCommitTimer.Tick += (_, _) =>
        {
            _ambientCommitTimer.Stop();
            (DataContext as MainViewModel)?.CommitAmbientLevel();
        };

        Loaded += (_, _) =>
        {
            _sliderFill = (Border?)AmbientSlider.Template.FindName("Fill", AmbientSlider);
            UpdateSliderFill();
        };
        AmbientSlider.ValueChanged += OnAmbientSliderChanged;
        AmbientSlider.SizeChanged += (_, _) => UpdateSliderFill();
    }

    /// <summary>
    /// The blue fill is an overlay, not the Track's decrease button: a Track-based fill
    /// always stops one (invisible) thumb width short of the end, so a maxed value still
    /// showed a gray stub on the right.
    /// </summary>
    private void UpdateSliderFill()
    {
        if (_sliderFill is null) return;
        double width = AmbientSlider.ActualWidth;
        if (width <= 0) return;
        double span = AmbientSlider.Maximum - AmbientSlider.Minimum;
        double frac = span <= 0 ? 0 : (AmbientSlider.Value - AmbientSlider.Minimum) / span;
        _sliderFill.Width = width * Math.Clamp(frac, 0, 1);
    }

    private void OnAmbientSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSliderFill();
        // Restart the debounce window on every value change (scrub tick or click).
        _ambientCommitTimer.Stop();
        _ambientCommitTimer.Start();
    }

    // Bar slider mouse input: the template has no Track, so press anywhere on the bar
    // jumps to that position and a held drag scrubs. Hit-testing lives here instead of in
    // the Track's narrow repeat-button/thumb strips.
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (AmbientSlider.IsEnabled && IsPointOnSlider(e.GetPosition(AmbientSlider)))
        {
            _scrubbing = true;
            AmbientSlider.CaptureMouse();
            ScrubTo(e.GetPosition(AmbientSlider).X);
            e.Handled = true;
            return;
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (_scrubbing)
        {
            ScrubTo(e.GetPosition(AmbientSlider).X);
            e.Handled = true;
            return;
        }
        base.OnPreviewMouseMove(e);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_scrubbing)
        {
            _scrubbing = false;
            AmbientSlider.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        base.OnPreviewMouseLeftButtonUp(e);
    }

    private bool IsPointOnSlider(Point p) =>
        p.X >= 0 && p.X <= AmbientSlider.ActualWidth && p.Y >= 0 && p.Y <= AmbientSlider.ActualHeight;

    private void ScrubTo(double x)
    {
        double width = AmbientSlider.ActualWidth;
        if (width <= 0) return;
        double frac = Math.Clamp(x / width, 0, 1);
        AmbientSlider.Value = AmbientSlider.Minimum + frac * (AmbientSlider.Maximum - AmbientSlider.Minimum);
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
}
