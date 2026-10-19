using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using SonyCtrl.Mdr;

namespace SonyCtrl.UI;

/// <summary>
/// Single ViewModel backing the tray icon, the flyout control panel and the context menu.
/// Holds no protocol logic; subscribes to ConnectionManager events and exposes UI state.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ConnectionManager _connection;
    private readonly ControlPanelWindow _panel;

    public MainViewModel(ConnectionManager connection)
    {
        _connection = connection;
        _panel = new ControlPanelWindow(this);

        _connection.StateChanged += OnStateChanged;
        _connection.BatteryChanged += OnBatteryChanged;
        _connection.AncChanged += OnAncChanged;
        _connection.DeviceColorChanged += OnDeviceColorChanged;
        _connection.UnresponsiveChanged += OnUnresponsiveChanged;

        TogglePanelCommand = new RelayCommand(_ => TogglePanel());
        ExitCommand = new RelayCommand(_ => Application.Current.Shutdown());
        SetAncCommand = new RelayCommand(param => _ = SetAncAsync(param));

        // Startup settle: after the initial connect + first state pull, hand the pages that
        // burst allocated back to the OS.
        MemoryTrim.ScheduleTrim(TimeSpan.FromSeconds(30));
    }

    // ---------- Commands ----------
    public ICommand TogglePanelCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand SetAncCommand { get; }

    private async Task SetAncAsync(object? param)
    {
        if (param is string modeName && Enum.TryParse<AncMode>(modeName, out var mode))
            await _connection.SetAncModeAsync(mode);
    }

    private void TogglePanel()
    {
        if (_panel.IsVisible) _panel.Hide();
        else
        {
            _panel.PositionNearTray();
            // Lay the frosted-glass backdrop before Show so the shot never contains the
            // panel itself; PositionNearTray must run first so the capture rect is final.
            _panel.CaptureBackdrop();
            _panel.Show();
            _panel.Activate();
            _panel.PlayEntranceAnimation();
            // SPEC §4.4: opening the panel pulls the full state so it never shows stale
            // data (e.g. a bud that went back into the case since the last refresh).
            _ = _connection.RefreshNowAsync();
        }
    }

    // ---------- UI state ----------
    public string DeviceName => _connection.DeviceName;

    public string StateText => _connection.ConnState switch
    {
        ConnectionManager.State.Connected => "已连接",
        ConnectionManager.State.Connecting => "连接中...",
        ConnectionManager.State.Reconnecting => "重连中...",
        _ => "未连接",
    };

    public bool IsConnected => _connection.ConnState == ConnectionManager.State.Connected;
    public bool IsUnresponsive => _connection.IsUnresponsive;

    public string BatteryLeftText => _connection.Battery is { HasLeft: true } b1 ? $"{b1.LeftPercent}%" : "--";
    public string BatteryRightText => _connection.Battery is { HasRight: true } b2 ? $"{b2.RightPercent}%" : "--";
    public string BatteryCaseText => _connection.Battery is { HasCase: true } b3 ? $"{b3.CasePercent}%" : "--";

    public Visibility LeftChargingVisible => ChargingToVis(_connection.Battery?.LeftCharging ?? 0);
    public Visibility RightChargingVisible => ChargingToVis(_connection.Battery?.RightCharging ?? 0);
    public Visibility CaseChargingVisible => ChargingToVis(_connection.Battery?.CaseCharging ?? 0);

    private static Visibility ChargingToVis(byte c) =>
        c is 1 or 3 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsNoiseCancelling => _connection.Anc?.Mode == AncMode.NoiseCancelling;
    public bool IsAmbientSound => _connection.Anc?.Mode == AncMode.AmbientSound;
    public bool IsAncOff => _connection.Anc is null || _connection.Anc.Mode == AncMode.Off;

    /// <summary>
    /// Official product render for the panel header, picked by the colour byte the
    /// device reports (same encoding as the cloud model-info API's model_color_id).
    /// Unknown/absent colour falls back to black.
    /// </summary>
    public ImageSource ProductImageSource => GetProductImage(_connection.DeviceColor);

    private static readonly Dictionary<byte, ImageSource> ProductImageCache = new();

    private static ImageSource GetProductImage(byte? color)
    {
        byte key = color switch
        {
            ModelColor.Silver => ModelColor.Silver,
            ModelColor.Pink => ModelColor.Pink,
            _ => ModelColor.Black,
        };

        if (!ProductImageCache.TryGetValue(key, out var source))
        {
            var bmp = new BitmapImage(new Uri($"pack://application:,,,/UI/Assets/xm5_{KeyName(key)}.png"));
            bmp.Freeze();
            ProductImageCache[key] = source = bmp;
        }
        return source;
    }

    private static string KeyName(byte color) => color switch
    {
        ModelColor.Silver => "silver",
        ModelColor.Pink => "pink",
        _ => "black",
    };

    /// <summary>
    /// Slider value. Updates only the local echo during drag; the actual SET command is
    /// sent from CommitAmbientLevel (wired to Thumb DragCompleted) to avoid flooding the
    /// device while dragging — rapid per-tick sends time out and flag "unresponsive".
    /// </summary>
    public int AmbientLevel
    {
        get => _ambientEcho ?? _connection.Anc?.AmbientLevel ?? 10;
        set
        {
            _ambientEcho = Math.Clamp(value, 0, 20);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AmbientLevelText));
        }
    }
    private int? _ambientEcho;

    public string AmbientLevelText => $"{AmbientLevel}/20";

    /// <summary>Send the pending slider value to the device (called on DragCompleted).</summary>
    public void CommitAmbientLevel()
    {
        if (_ambientEcho is int level)
        {
            _ambientEcho = null;
            _ = _connection.SetAmbientLevelAsync((byte)level);
        }
    }

    public bool AmbientLevelEnabled => _connection.Anc?.Mode == AncMode.AmbientSound;

    /// <summary>
    /// Ambient voice focus (Sound Connect「关注语音」). Getter mirrors the device state
    /// (NTFY-driven); the setter runs on user clicks (TwoWay binding) and sends the SET —
    /// no echo loop because source→target updates never call the setter.
    /// </summary>
    public bool IsVoiceFocus
    {
        get => _connection.Anc is { Mode: AncMode.AmbientSound, AmbientMode: AmbientModeKind.Voice };
        set => _ = _connection.SetAmbientVoiceFocusAsync(value);
    }

    /// <summary>Voice focus only exists in Ambient mode; grayed out otherwise (like the slider).</summary>
    public bool VoiceFocusEnabled => AmbientLevelEnabled;

    // ---------- Tray icon ----------
    private static readonly string IconCachePath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "SonyHeadphonesController.tray.ico");

    // Frozen icon instances keyed by (text, colour): the 15s fallback refresh re-raises
    // BatteryChanged/AncChanged even when nothing changed, and redrawing through the whole
    // RenderTargetBitmap→PNG→ICO→reload pipeline each time was the app's biggest idle
    // allocation source. Rebuild only when the icon content actually changes.
    private (string Text, Color Colour)? _iconKey;
    private ImageSource _iconSource = null!;

    /// <summary>Tray icon drawn at runtime: battery percentage + colour coding.</summary>
    public ImageSource TrayIconSource
    {
        get
        {
            var b = _connection.Battery;
            // Lowest of the known buds only — a missing bud (-1) is not a 0% reading.
            int pct = b?.TrayPercent ?? -1;

            var colour = !IsConnected ? Colors.Gray
                : pct < 0 ? Colors.Gray
                : pct <= 20 ? Colors.Red
                : pct <= 50 ? Colors.Orange
                : Colors.SeaGreen;

            string text = !IsConnected || pct < 0 ? "-" : pct.ToString();

            if (_iconKey is (string kText, Color kColour) && kText == text && kColour == colour)
                return _iconSource;

            // H.NotifyIcon's pipeline ends in System.Drawing.Icon(Stream), which requires a real
            // .ico file. Write an ICO with an embedded PNG (Vista+ format) and load as BitmapImage.
            DrawIconToIcoFile(text, colour, IconCachePath);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(IconCachePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            _iconKey = (text, colour);
            _iconSource = bmp;
            return bmp;
        }
    }

    private static void DrawIconToIcoFile(string text, Color colour, string path)
    {
        const int size = 32;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            dc.DrawRoundedRectangle(brush, null, new Rect(2, 2, size - 4, size - 4), 6, 6);

            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var formatted = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                text.Length > 2 ? 13 : 16,
                Brushes.White,
                96);
            dc.DrawText(formatted, new Point((size - formatted.Width) / 2, (size - formatted.Height) / 2));
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var png = new System.IO.MemoryStream();
        encoder.Save(png);
        byte[] pngBytes = png.ToArray();

        // Minimal ICO container: ICONDIR(6) + ICONDIRENTRY(16) + PNG payload.
        // PNG-in-ICO is supported since Windows Vista.
        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create);
        using var bw = new System.IO.BinaryWriter(fs);
        bw.Write((ushort)0);      // reserved
        bw.Write((ushort)1);      // type: icon
        bw.Write((ushort)1);      // count
        bw.Write((byte)size);     // width
        bw.Write((byte)size);     // height
        bw.Write((byte)0);        // palette
        bw.Write((byte)0);        // reserved
        bw.Write((ushort)1);      // color planes
        bw.Write((ushort)32);     // bits per pixel
        bw.Write((uint)pngBytes.Length); // data size
        bw.Write((uint)22);       // data offset (6 + 16)
        bw.Write(pngBytes);
    }

    // ---------- Event plumbing ----------
    private void OnStateChanged(ConnectionManager.State _) { RefreshAll(); }
    private void OnBatteryChanged(BatteryStatus _) { RefreshAll(); }
    private void OnAncChanged(AncState _) { _ambientEcho = null; RefreshAll(); }
    private void OnDeviceColorChanged(byte? _) { RefreshAll(); }
    private void OnUnresponsiveChanged() { RefreshAll(); }

    private void RefreshAll()
    {
        foreach (var name in new[]
        {
                 nameof(DeviceName), nameof(StateText), nameof(IsConnected), nameof(IsUnresponsive),
                 nameof(BatteryLeftText), nameof(BatteryRightText), nameof(BatteryCaseText),
                 nameof(LeftChargingVisible), nameof(RightChargingVisible), nameof(CaseChargingVisible),
                 nameof(IsNoiseCancelling), nameof(IsAmbientSound), nameof(IsAncOff),
                 nameof(AmbientLevel), nameof(AmbientLevelText), nameof(AmbientLevelEnabled),
                 nameof(IsVoiceFocus), nameof(VoiceFocusEnabled),
                 nameof(ProductImageSource),
                 nameof(TrayIconSource), nameof(TrayToolTip),
             })
        {
            OnPropertyChanged(name);
        }
        // H.NotifyIcon doesn't auto-refresh when the bound VM property changes;
        // reassign through the control so the shell icon/tooltip update. Reference-guard
        // both: on cache hits the property returns the same frozen instance, and pushing
        // it through the IconSource setter anyway would rerun the ImageSource→Icon
        // conversion and shell update for nothing on every 15s refresh.
        // H.NotifyIcon doesn't auto-refresh when the bound VM property changes;
        // reassign through the control so the shell icon/tooltip update. Reference-guard
        // both: on cache hits the property returns the same frozen instance, and pushing
        // it through the IconSource setter anyway would rerun the ImageSource→Icon
        // conversion and shell update for nothing on every 15s refresh.
        if (TrayIconOwner is not null)
        {
            if (TrayIconOwner.ToolTipText != TrayToolTip)
                TrayIconOwner.ToolTipText = TrayToolTip;
            if (!ReferenceEquals(TrayIconOwner.IconSource, TrayIconSource))
                TrayIconOwner.IconSource = TrayIconSource;
        }
    }

    /// <summary>
    /// Dynamic tooltip text for the tray icon. Multi-line while connected so the
    /// battery levels are visible without opening the panel.
    /// </summary>
    public string TrayToolTip => IsConnected
        ? $"✅已连接\n左：{BatteryLeftText}\n右：{BatteryRightText}\n机仓：{BatteryCaseText}"
        : $"{DeviceName} — {StateText}";

    /// <summary>Set by TrayHostWindow so the VM can poke the TaskbarIcon.</summary>
    internal TaskbarIcon? TrayIconOwner { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _connection.StateChanged -= OnStateChanged;
        _connection.BatteryChanged -= OnBatteryChanged;
        _connection.AncChanged -= OnAncChanged;
        _connection.DeviceColorChanged -= OnDeviceColorChanged;
        _connection.UnresponsiveChanged -= OnUnresponsiveChanged;
        _panel.Close();
    }
}

/// <summary>Minimal ICommand implementation.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    public RelayCommand(Action<object?> execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter);
}
