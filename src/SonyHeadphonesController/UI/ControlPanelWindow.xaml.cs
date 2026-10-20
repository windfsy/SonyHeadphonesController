using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SonyCtrl.UI;

public partial class ControlPanelWindow : Window
{
    private readonly DispatcherTimer _ambientCommitTimer;
    private Border? _sliderFill;
    private Border? _ancKnob;
    private TranslateTransform? _ancKnobTransform;
    private int _ancKnobIndex = -1;   // segment the knob was last placed at (-1 = never placed)
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
            _sliderFill ??= (Border?)AmbientSlider.Template.FindName("Fill", AmbientSlider);
            UpdateSliderFill();
            UpdateAncKnob();
        };
        AmbientSlider.ValueChanged += OnAmbientSliderChanged;
        AmbientSlider.SizeChanged += (_, _) => UpdateSliderFill();

        // Reposition the ANC knob whenever the connection/ANC state changes.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsNoiseCancelling)
                or nameof(MainViewModel.IsAmbientSound) or nameof(MainViewModel.IsAncOff))
                UpdateAncKnob();
        };
    }

    /// <summary>
    /// ANC segmented control: slide the knob to the segment matching the current mode.
    /// Segment order matches the Grid columns: NoiseCancelling(0), AmbientSound(1), Off(2).
    /// The knob's layout position stays at the first segment; mode changes move it through
    /// a RenderTransform animation so the selection glides instead of jumping. The very
    /// first placement is an instant jump (sliding in on first open would look random).
    /// </summary>
    private void UpdateAncKnob()
    {
        if (DataContext is not MainViewModel vm) return;
        // Same lazy resolution as the toggle parts: Loaded can fire before the template
        // is instantiated, so retry until the visual tree exists.
        _ancKnob ??= AncKnob;
        if (_ancKnobTransform is null && _ancKnob is not null)
        {
            _ancKnobTransform = new TranslateTransform();
            _ancKnob.RenderTransform = _ancKnobTransform;
        }
        if (_ancKnob is null || _ancKnobTransform is null
            || _ancKnob.Parent is not Grid container)
            return;
        // Measure the track from its container (window width is fixed but measure anyway);
        // the knob is one segment wide minus a 3px inset on each side.
        double trackWidth = container.ActualWidth > 0 ? container.ActualWidth : container.DesiredSize.Width;
        if (trackWidth <= 0) return;
        double segmentWidth = trackWidth / 3;
        int index = vm.IsNoiseCancelling ? 0 : vm.IsAmbientSound ? 1 : 2;

        _ancKnob.Width = segmentWidth - 6;
        // The 15s fallback refresh re-raises the ANC properties even when the mode did
        // not change; without this guard every refresh would restart a pointless animation.
        if (index == _ancKnobIndex) return;
        bool wasPlaced = _ancKnobIndex >= 0;
        _ancKnobIndex = index;

        _ancKnob.Margin = new Thickness(3, 0, 0, 0);
        double target = index * segmentWidth;
        if (wasPlaced)
        {
            _ancKnobTransform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
        }
        else
        {
            // A held animation would keep overriding the base value; clear it for the jump.
            _ancKnobTransform.BeginAnimation(TranslateTransform.XProperty, null);
            _ancKnobTransform.X = target;
        }
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

    /// <summary>
    /// Frosted-glass backdrop: capture the screen region behind the card (before Show, so
    /// the panel itself is never in the shot), blur it separably in C#, lift it adaptively
    /// toward white, and lay it in as the card's Background. Layered windows
    /// (AllowsTransparency) cannot use the DWM system blur, so this snapshot approximation
    /// is the only way to get the effect while keeping the custom rounded corners. The
    /// card rect is computed from the window geometry (Left/Top/Width + measured height)
    /// instead of PointToScreen, which fails before the first Show (no PresentationSource
    /// yet). The flyout always anchors near the tray on the primary taskbar, so the
    /// primary screen's DPI is the right DIP→physical mapping. Failure (secure desktop,
    /// driver oddities) keeps the XAML fallback background.
    /// </summary>
    public void CaptureBackdrop()
    {
        try
        {
            // Card rect in DIPs: window rect inset by the card's 8 Margin. PositionNearTray
            // must run first; on first open ActualHeight is still 0 and the height comes
            // from the Measure it performed.
            double winHeight = ActualHeight > 0 ? ActualHeight : Card.DesiredSize.Height;
            var margin = Card.Margin;
            double dipX = Left + margin.Left;
            double dipY = Top + margin.Top;
            double dipW = Width - margin.Left - margin.Right;
            double dipH = winHeight - margin.Top - margin.Bottom;
            if (dipW <= 0 || dipH <= 0)
                return;

            float scale;
            using (var screen = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                scale = screen.DpiX / 96f;

            int x = (int)Math.Round(dipX * scale);
            int y = (int)Math.Round(dipY * scale);
            int w = (int)Math.Round(dipW * scale);
            int h = (int)Math.Round(dipH * scale);

            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                // CopyFromScreen throws on the secure desktop; the outer try covers it.
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
            }

            // Downscale to ~120px on the long edge before blurring: heavy frost where the
            // wallpaper only survives as gentle gradients, and the tiny buffer keeps the
            // separable blur cheap. A fixed sample size also keeps the blur kernel visually
            // constant across DPIs.
            const int sample = 120;
            double downscale = Math.Min(1.0, sample / (double)Math.Max(w, h));
            int sw = Math.Max(4, (int)(w * downscale));
            int sh = Math.Max(4, (int)(h * downscale));

            var bytes = GetBitmapBytes(bmp, out int stride);
            var blurred = BlurBytes(bytes, w, h, stride, sw, sh);

            // Adaptive white lift: the panel's gray label text needs a light background
            // whatever the wallpaper, so lift every pixel toward white by a per-capture
            // amount that lands the snapshot's average on the target lightness — dark
            // wallpapers lift hard, bright ones barely. The compressed blurred colour
            // structure stays visible, which is what reads as frosted glass. The tint is
            // baked into an opaque brush on purpose: any alpha < 1 would blend the dark
            // raw pixels back over the lift and re-darken the card.
            // Pin the lift instead of deriving it from the snapshot average: the blurred
            // wallpaper still shows through, but the card's effective brightness is
            // constant regardless of wallpaper, so the fixed dark text colours below
            // always read. (Adaptive lift previously tuned to the wallpaper's average,
            // which could push the card dark enough to swallow #888 gray labels.)
            // Opaque brush is a hard requirement: a translucent card would composite the
            // UNBLURRED live screen over the lifted snapshot (alpha<1 re-darkens it into
            // the flat gray seen on 2026-10-20), and the frost structure would be lost.
            const int targetLuma = 205;
            double lift = targetLuma / 255.0;
            int liftNum = (int)(lift * 255);

            // Rasterize at the downscaled size and let Stretch.Fill expand it back.
            int strideOut = sw * 4;
            var pixels = new byte[strideOut * sh];
            for (int row = 0; row < sh; row++)
            {
                int srcBase = row * sw * 4;
                for (int col = 0; col < sw; col++)
                {
                    int s = srcBase + col * 4;
                    int d = row * strideOut + col * 4;
                    pixels[d] = (byte)(blurred[s] + (255 - blurred[s]) * liftNum / 255);
                    pixels[d + 1] = (byte)(blurred[s + 1] + (255 - blurred[s + 1]) * liftNum / 255);
                    pixels[d + 2] = (byte)(blurred[s + 2] + (255 - blurred[s + 2]) * liftNum / 255);
                    pixels[d + 3] = 0xFF;
                }
            }

            var src = BitmapSource.Create(sw, sh, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
                pixels, strideOut);
            src.Freeze();
            var brush = new ImageBrush(src) { Stretch = Stretch.Fill };
            brush.Freeze();
            Card.Background = brush;
        }
        catch (Exception ex)
        {
            // Diagnostics only — the panel still shows with the plain fallback background.
            try
            {
                var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SonyHeadphonesController.diag.log");
                System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] CaptureBackdrop failed: {ex}\n");
            }
            catch { /* logging must never break the panel */ }
        }
    }

    private static byte[] GetBitmapBytes(System.Drawing.Bitmap bmp, out int stride)
    {
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            stride = data.Stride;
            var bytes = new byte[data.Stride * bmp.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// Separable box-blur (4 passes ≈ gaussian) on BGRA bytes. Radius is fixed in
    /// downscaled-pixel space (≈12px) so the frosted look does not drift with DPI.
    /// </summary>
    private static byte[] BlurBytes(byte[] src, int w, int h, int stride, int sw, int sh)
    {
        // Nearest-neighbour downsample into a tightly packed sw×sh BGRA buffer.
        var small = new byte[sw * sh * 4];
        for (int y = 0; y < sh; y++)
        {
            int sy = Math.Min(h - 1, y * h / sh);
            for (int x = 0; x < sw; x++)
            {
                int sx = Math.Min(w - 1, x * w / sw);
                int s = sy * stride + sx * 4;
                int d = (y * sw + x) * 4;
                small[d] = src[s];
                small[d + 1] = src[s + 1];
                small[d + 2] = src[s + 2];
                small[d + 3] = 0xFF;
            }
        }

        var tmp = new byte[small.Length];
        BoxPass(small, tmp, sw, sh, horizontal: true);
        BoxPass(tmp, small, sw, sh, horizontal: false);
        BoxPass(small, tmp, sw, sh, horizontal: true);
        BoxPass(tmp, small, sw, sh, horizontal: false);
        return small;
    }

    private static void BoxPass(byte[] input, byte[] output, int w, int h, bool horizontal)
    {
        const int radius = 12;
        int outer = horizontal ? h : w;   // index of the pass dimension
        int inner = horizontal ? w : h;   // length of each run
        int stepIn = horizontal ? 4 : w * 4;
        int stepOut = horizontal ? 4 : w * 4;

        for (int o = 0; o < outer; o++)
        {
            int baseIdx = horizontal ? o * w * 4 : o * 4;
            for (int c = 0; c < 4; c++)
            {
                long sum = 0;
                for (int k = -radius; k <= radius; k++)
                {
                    int i = Math.Clamp(k, 0, inner - 1);
                    sum += input[baseIdx + i * stepIn + c];
                }
                for (int i = 0; i < inner; i++)
                {
                    output[baseIdx + i * stepOut + c] = (byte)(sum / (radius * 2 + 1));
                    int addIdx = Math.Clamp(i + radius + 1, 0, inner - 1);
                    int subIdx = Math.Clamp(i - radius, 0, inner - 1);
                    sum += input[baseIdx + addIdx * stepIn + c] - input[baseIdx + subIdx * stepIn + c];
                }
            }
        }
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
        // The show cycle (backdrop capture, blur buffers, animations) inflates the working
        // set; give it back once the panel goes idle-hidden.
        MemoryTrim.ScheduleTrim(TimeSpan.FromSeconds(2));
    }
}
