using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using H.NotifyIcon;

namespace SonyCtrl.UI;

/// <summary>
/// Hidden host window: provides the message pump + HWND for WM_DEVICECHANGE (DeviceWatcher).
/// The TaskbarIcon itself lives in App.xaml resources (instantiated at startup, independent
/// of any window's visual tree, per H.NotifyIcon's recommended pattern).
/// </summary>
public partial class TrayHostWindow : Window
{
    private MainViewModel? _vm;

    public TrayHostWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Force-create the window's HWND (without showing) so DeviceWatcher can attach its
    /// HwndSource hook and call RegisterDeviceNotification. Must run before the
    /// ConnectionManager constructor.
    /// </summary>
    public void EnsureHandleForWatcher()
    {
        var helper = new WindowInteropHelper(this);
        _ = helper.EnsureHandle();
    }

    public void Initialize(ConnectionManager connection)
    {
        _vm = new MainViewModel(connection);

        var tray = (TaskbarIcon)Application.Current.FindResource("TrayIcon");
        tray.DataContext = _vm;
        tray.LeftClickCommand = _vm.TogglePanelCommand;
        tray.IconSource = _vm.TrayIconSource;
        tray.ToolTipText = _vm.TrayToolTip;

        // Context menu with commands bound to the VM.
        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = "打开控制面板", Command = _vm.TogglePanelCommand };
        var exitItem = new MenuItem { Header = "退出", Command = _vm.ExitCommand };
        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        tray.ContextMenu = menu;

        _vm.TrayIconOwner = tray;

        // TaskbarIcon registers the shell icon in its Loaded event, which never fires for
        // an instance living in App.Resources (no visual tree). Force the same registration
        // path explicitly (H.NotifyIcon exposes ForceCreate for exactly this scenario).
        var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SonyHeadphonesController.diag.log");
        try
        {
            System.IO.File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] before ForceCreate: IconSource={tray.IconSource?.ToString() ?? "null"}\n");
            tray.ForceCreate(false);
            System.IO.File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] ForceCreate OK; IsCreated={tray.IsCreated}\n");
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] ForceCreate failed: {ex}\n");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm?.Dispose();
        base.OnClosed(e);
    }
}
