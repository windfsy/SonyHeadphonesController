using System.Windows;
using System.Windows.Threading;
using SonyCtrl.Mdr;
using SonyCtrl.UI;

namespace SonyCtrl;

/// <summary>
/// Owns the connection lifecycle and state synchronization.
///
/// State machine: Disconnected -> Connecting -> Connected -> (link lost) -> Reconnecting -> ...
/// Triggers:
///   - DeviceWatcher arrival/removal matching the configured target MAC
///   - MDR reader thread exit (link loss)
///   - 30s poll while Disconnected (fallback for missed WM_DEVICECHANGE; SPEC §7)
/// Behaviour:
///   - Backoff retry 1s -> 5s -> 30s cap while Reconnecting (SPEC §4.3)
///   - Full state pull after connect and on panel open; 15s fallback refresh timer (SPEC §4.4)
///   - All client commands serialized (protocol allows one in flight); guard retries once,
///     then marks Unresponsive; never guess-resend (SPEC §4.5)
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private static readonly int[] BackoffMs = { 1_000, 5_000, 30_000 };
    private const int PollDisconnectedMs = 30_000;
    // Fallback state refresh while connected. The device pushes NTFY for most changes but
    // NOT for "a bud went back into the case" (no such notification exists), so absence is
    // only discoverable by polling; 15s keeps that lag tolerable. Commands are serialized
    // (see _commandGate), so this cadence cannot collide with user actions.
    private const int FallbackRefreshMs = 15_000;

    public enum State { Disconnected, Connecting, Connected, Reconnecting }

    private readonly Dispatcher _dispatcher;
    private readonly DeviceWatcher _watcher;
    private readonly DispatcherTimer _pollTimer;   // 30s: fallback check when disconnected
    private readonly DispatcherTimer _refreshTimer; // 15s: fallback state refresh when connected

    // The protocol allows one command in flight (MdrConnection throws otherwise); serialize
    // every client operation — timer refresh, panel-open pull and user commands alike.
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    private MdrClient? _client;
    private State _state = State.Disconnected;
    private int _backoffIndex;
    private bool _disposed;

    // ---- State exposed to UI ----
    public State ConnState
    {
        get => _state;
        private set { _state = value; StateChanged?.Invoke(value); }
    }
    public BatteryStatus? Battery { get; private set; }
    public AncState? Anc { get; private set; }
    public bool IsUnresponsive { get; private set; }
    public string DeviceName => App.Config.TargetName ?? "未知设备";

    public event Action<State>? StateChanged;
    public event Action<BatteryStatus>? BatteryChanged;
    public event Action<AncState>? AncChanged;
    public event Action? UnresponsiveChanged;

    public ConnectionManager(AppConfig config, Window host)
    {
        _dispatcher = host.Dispatcher;

        _watcher = new DeviceWatcher(host);
        _watcher.DeviceArrived += OnDeviceArrived;
        _watcher.DeviceRemoved += OnDeviceRemoved;
        _watcher.Start();

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PollDisconnectedMs),
        };
        _pollTimer.Tick += (_, _) => PollFallback();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(FallbackRefreshMs),
        };
        _refreshTimer.Tick += (_, _) => RefreshStateFallback();

        // Startup: check if the target is already connected; otherwise wait for events.
        _ = Task.Run(InitialCheckAsync);

        _pollTimer.Start();
    }

    private ulong Target => App.Config.TargetAddress ?? 0;

    // ---------- Event handlers ----------

    private void OnDeviceArrived(ulong mac)
    {
        if (mac != Target) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (ConnState is State.Disconnected or State.Reconnecting)
                _ = ConnectAsync();
        });
    }

    private void OnDeviceRemoved(ulong mac)
    {
        if (mac != Target) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (ConnState == State.Connected)
                HandleLinkLoss("设备已断开");
            else if (ConnState is State.Connecting or State.Reconnecting)
                // The OS link is gone; stop chasing it instead of spinning in the
                // backoff loop (which previously left the UI stuck on 重连中 forever).
                ConnState = State.Disconnected;
        });
    }

    private async Task InitialCheckAsync()
    {
        // On startup the earbuds may already be connected (audio channel up):
        // poll the paired-device list once and connect if present.
        if (await TargetConnectedOnPcAsync())
        {
            Log("initial check: target connected on PC");
            await _dispatcher.InvokeAsync(() =>
            {
                if (ConnState == State.Disconnected)
                    _ = ConnectAsync();
            });
        }
        else
        {
            Log("initial check: target not connected on PC");
        }
    }

    /// <summary>
    /// True when the Windows paired-device table reports the target as connected, i.e. the
    /// OS already holds a link (audio channel up). Local table lookup only — no radio
    /// activity, so it can never disturb a link the earbuds have with another host.
    /// </summary>
    private static async Task<bool> TargetConnectedOnPcAsync()
    {
        ulong target = App.Config.TargetAddress ?? 0;
        if (target == 0) return false;
        return await Task.Run(() => Transport.BluetoothDeviceEnumerator.EnumeratePairedDevices()
            .Any(d => d.Address == target && d.Connected));
    }

    private void PollFallback()
    {
        // Covers missed WM_DEVICECHANGE arrivals: if the target shows as connected
        // while we're Disconnected/Reconnecting, start connecting.
        if (ConnState is State.Disconnected or State.Reconnecting && Target != 0)
        {
            _ = Task.Run(async () =>
            {
                bool present = await TargetConnectedOnPcAsync();
                Log($"poll: target connected={present}");
                if (present)
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (ConnState is State.Disconnected or State.Reconnecting)
                            _ = ConnectAsync();
                    });
                }
            });
        }
    }

    // ---------- Connect / disconnect ----------

    private async Task ConnectAsync()
    {
        if (ConnState is State.Connecting or State.Connected) return;

        // Only open the control channel while Windows already holds a link to the earbuds
        // (SPEC §4.3: the audio channel being up is the trigger). An RFCOMM connect to a
        // device the OS reports as absent would page it over the air and could pull it
        // away from the host it is currently with (e.g. the user's phone).
        if (!await TargetConnectedOnPcAsync())
        {
            Log("connect skipped: OS reports target not connected");
            if (ConnState != State.Disconnected) ConnState = State.Disconnected;
            return;
        }

        Log($"connect begin (state was {ConnState})");
        ConnState = State.Connecting;
        _backoffIndex = 0;

        var client = new MdrClient();
        try
        {
            await Task.Run(() => client.Connect(Target));
        }
        catch (Exception ex)
        {
            Log($"connect failed: {ex.Message}");
            client.Dispose();
            ScheduleReconnect();
            return;
        }

        Log("socket+INIT ok");
        _client = client;
        client.OnBatteryNotification += b => _dispatcher.BeginInvoke(() =>
        {
            MergeBattery(b);
            BatteryChanged?.Invoke(Battery!);
        });
        client.OnAncNotification += s => _dispatcher.BeginInvoke(() =>
        {
            Log($"ANC NTFY: {s}");
            if (s.Mode == AncMode.AmbientSound)
                _lastKnownAmbientLevel = s.AmbientLevel;
            Anc = s;
            AncChanged?.Invoke(s);
        });

        // The reader thread's exit means link loss; surface it on the UI thread.
        client.LinkLost += () => _dispatcher.BeginInvoke(() => HandleLinkLoss("链路中断"));

        ConnState = State.Connected;
        IsUnresponsive = false;
        UnresponsiveChanged?.Invoke();

        // Full state pull after connect (SPEC §4.4).
        await RefreshStateAsync();

        _refreshTimer.Start();
    }

    private void ScheduleReconnect()
    {
        ConnState = State.Reconnecting;
        int delay = BackoffMs[Math.Min(_backoffIndex, BackoffMs.Length - 1)];
        _backoffIndex++;
        Log($"reconnect scheduled in {delay} ms (attempt {_backoffIndex})");

        _ = _dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(delay);
            if (!_disposed && ConnState == State.Reconnecting)
                _ = ConnectAsync();
        }, DispatcherPriority.Background);
    }

    private void HandleLinkLoss(string reason)
    {
        if (ConnState is not (State.Connected or State.Connecting)) return;

        Log($"link loss: {reason}");
        CleanupClient();
        _refreshTimer.Stop();
        IsUnresponsive = false;

        // If the audio link is still up this was a control-channel hiccup: retry soon.
        // Otherwise wait for the next arrival event / poll.
        _ = Task.Run(async () =>
        {
            bool stillConnected = Transport.BluetoothDeviceEnumerator.EnumeratePairedDevices()
                .Any(d => d.Address == Target && d.Connected);
            await _dispatcher.InvokeAsync(() =>
            {
                if (stillConnected)
                {
                    _backoffIndex = 0;
                    ScheduleReconnect();
                }
                else
                {
                    ConnState = State.Disconnected;
                }
            });
        });
    }

    private void CleanupClient()
    {
        _client?.Dispose();
        _client = null;
    }

    // File diagnostics: tray app has no console; %TEMP% log is the only observable channel.
    private static readonly string DiagLog = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "SonyHeadphonesController.diag.log");
    internal static void Log(string msg) =>
        System.IO.File.AppendAllText(DiagLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");

    // ---------- State refresh & commands (guarded, SPEC §4.5) ----------

    private async Task RefreshStateAsync()
    {
        if (_client is null) return;

        var battery = await GuardedAsync(c => c.GetBattery());
        if (battery is not null)
        {
            Log($"battery GET: {battery}");
            MergeBattery(battery);
            BatteryChanged?.Invoke(Battery!);
        }

        var anc = await GuardedAsync(c => c.GetAncMode());
        if (anc is not null)
        {
            Log($"ANC GET: {anc}");
            Anc = anc;
            AncChanged?.Invoke(anc);
        }
    }

    private void RefreshStateFallback()
    {
        if (ConnState == State.Connected)
            _ = RefreshStateAsync();
    }

    /// <summary>
    /// Run an MdrClient operation with the command guard: one retry, then mark unresponsive.
    /// Runs on a background thread (SendCommand blocks with spin-wait); marshals nothing itself.
    /// Serialized through _commandGate: the protocol carries one command at a time, and an
    /// "already in flight" throw would otherwise be misread as a device failure.
    /// </summary>
    private async Task<T?> GuardedAsync<T>(Func<MdrClient, T> op) where T : class
    {
        if (_client is null) return null;

        T? result = null;
        await _commandGate.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var client = _client;
                if (client is null || !client.IsConnected) break;
                try
                {
                    result = await Task.Run(() => op(client));
                    break;
                }
                catch (Exception)
                {
                    result = null;
                }
            }
        }
        finally
        {
            _commandGate.Release();
        }

        bool unresponsive = result is null;
        if (unresponsive != IsUnresponsive)
        {
            IsUnresponsive = unresponsive;
            _ = _dispatcher.BeginInvoke(() => UnresponsiveChanged?.Invoke());
        }
        return result;
    }

    /// <summary>
    /// Full state pull for UI-driven moments (panel opened). Serialized with everything
    /// else through the command gate; a queued pull waits its turn rather than being
    /// dropped, so opening the panel always ends on fresh data.
    /// </summary>
    public Task RefreshNowAsync() =>
        ConnState == State.Connected ? RefreshStateAsync() : Task.CompletedTask;

    /// <summary>
    /// UI command: switch ANC to one of the three modes (level remembered for Ambient).
    /// The device confirms via NTFY_PARAM, which updates Anc through OnAncNotification —
    /// deliberately NO GET read-back here (matches Sound Connect's behaviour in an HCI
    /// capture: SET → NTFY only). An immediate GET can land mid-transition, and while the
    /// buds are unworn the device auto-pauses NC and GET reports OFF — reading back would
    /// overwrite the correct NTFY state and make the UI snap back to "关闭".
    /// </summary>
    public async Task SetAncModeAsync(AncMode mode)
    {
        byte level;
        if (mode == AncMode.AmbientSound)
            // Keep the level we last heard from the device (or user's last set) instead of
            // resetting to 10 every time the user re-enters ambient mode.
            level = _lastKnownAmbientLevel ?? (Anc?.AmbientLevel ?? 10);
        else
            level = (byte)0;

        await GuardedAsync(c => { Log($"ANC SET -> {mode} level={level}"); c.SetAncMode(mode, level); return (object)true; });
    }

    /// <summary>UI command: adjust ambient level (only meaningful in Ambient mode).</summary>
    public async Task SetAmbientLevelAsync(byte level)
    {
        if (level > 20) level = 20;
        _lastKnownAmbientLevel = level;
        await GuardedAsync(c => { Log($"ANC SET level -> {level}"); c.SetAncMode(AncMode.AmbientSound, level); return (object)true; });
    }

    /// <summary>Last ambient level seen from device or set by user; null = unknown (use 10).</summary>
    private byte? _lastKnownAmbientLevel;

    // Battery messages arrive per slot group (L/R vs cradle). Overwrite only the groups
    // the message carried — including its -1 (absent bud) readings — so a bud going into
    // the case flips to "--" instead of keeping its last percentage forever.
    private void MergeBattery(BatteryStatus incoming)
    {
        var current = Battery ?? new BatteryStatus();
        if (incoming.CarriesLeftRight)
        {
            current.LeftPercent = incoming.LeftPercent;
            current.LeftCharging = incoming.LeftCharging;
            current.RightPercent = incoming.RightPercent;
            current.RightCharging = incoming.RightCharging;
            current.CarriesLeftRight = true;
        }
        if (incoming.CarriesCase)
        {
            current.CasePercent = incoming.CasePercent;
            current.CaseCharging = incoming.CaseCharging;
            current.CarriesCase = true;
        }
        Battery = current;
    }

    public void Dispose()
    {
        _disposed = true;
        _pollTimer.Stop();
        _refreshTimer.Stop();
        _watcher.Dispose();
        CleanupClient();
    }
}
